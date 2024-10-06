using Common.Logging;
using Ipfs;
using Ipfs.Core;
using Ipfs.CoreApi;
using PeerTalk.Protocols;
using ProtoBuf;
using Semver;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PeerTalk.Routing
{
    /// <summary>
    ///   DHT Protocol version 1.0
    /// </summary>
    public class Dht1 : IPeerProtocol, IService, IPeerRouting, IContentRouting, IDistibutedValueStore
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(Dht1));

        /// <inheritdoc />
        public string Name { get; } = "ipfs/kad";

        /// <inheritdoc />
        public SemVersion Version { get; } = new SemVersion(1, 0);

        public required IStore<MultiHash, byte[]> Store { get; set; }

        /// <summary>
        ///   Provides access to other peers.
        /// </summary>
        public required Swarm Swarm { get; set; }

        /// <summary>
        ///  Routing information on peers.
        /// </summary>
        public RoutingTable RoutingTable = default!;

        /// <summary>
        ///   Peers that can provide some content.
        /// </summary>
        public ContentRouter ContentRouter = default!;

        /// <summary>
        ///   The number of closer peers to return.
        /// </summary>
        /// <value>
        ///   Defaults to 20.
        /// </value>
        public int CloserPeerCount { get; set; } = 20;

        /// <summary>
        ///   Raised when the DHT is stopped.
        /// </summary>
        /// <seealso cref="StopAsync"/>
        public event EventHandler Stopped = default!;

        /// <inheritdoc />
        public override string ToString()
        {
            return $"/{Name}/{Version}";
        }

        /// <inheritdoc />
        public async Task ProcessMessageAsync(PeerConnection connection, Stream stream, CancellationToken cancel = default)
        {
            while (true)
            {
                var request = await ProtoBufHelper.ReadMessageAsync<DhtMessage>(stream, cancel).ConfigureAwait(false);

                log.Debug($"got {request.Type} from {connection.RemotePeer}");
                var response = new DhtMessage
                {
                    Type = request.Type,
                    ClusterLevelRaw = request.ClusterLevelRaw
                };
                switch (request.Type)
                {
                    case MessageType.Ping:
                        response = ProcessPing(request, response);
                        break;
                    case MessageType.FindNode:
                        response = ProcessFindNode(request, response);
                        break;
                    case MessageType.GetProviders:
                        response = ProcessGetProviders(request, response);
                        break;
                    case MessageType.AddProvider:
                        response = ProcessAddProvider(connection.RemotePeer, request, response);
                        break;
                    case MessageType.PutValue:
                        response = await ProcessPutValue(connection.RemotePeer, request, response);
                        break;
                    case MessageType.GetValue:
                        response = await ProcessGetValueAsync(connection.RemotePeer, request, response);
                        break;
                    case MessageType.FindSimilarValues:
                        response = ProcessFindSimilarValues(connection.RemotePeer, request, response);
                        break;
                    default:
                        log.Debug($"unknown {request.Type} from {connection.RemotePeer}");
                        // TODO: Should we close the stream?
                        continue;
                }
                if (response != null)
                {
                    ProtoBuf.Serializer.SerializeWithLengthPrefix(stream, response, PrefixStyle.Base128);
                    await stream.FlushAsync(cancel).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public Task StartAsync()
        {
            log.Debug("Starting");

            RoutingTable = new RoutingTable(Swarm.LocalPeer);
            ContentRouter = new ContentRouter();
            Swarm.AddProtocol(this);
            Swarm.PeerDiscovered += Swarm_PeerDiscovered;
            Swarm.PeerRemoved += Swarm_PeerRemoved;
            foreach (var peer in Swarm.KnownPeers)
            {
                RoutingTable.Add(peer);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync()
        {
            log.Debug("Stopping");

            Swarm.RemoveProtocol(this);
            Swarm.PeerDiscovered -= Swarm_PeerDiscovered;
            Swarm.PeerRemoved -= Swarm_PeerRemoved;

            Stopped?.Invoke(this, EventArgs.Empty);
            ContentRouter?.Dispose();
            return Task.CompletedTask;
        }

        /// <summary>
        ///   The swarm has discovered a new peer, update the routing table.
        /// </summary>
        private void Swarm_PeerDiscovered(object sender, Peer e)
        {
            RoutingTable.Add(e);
        }

        /// <summary>
        ///   The swarm has removed a peer, update the routing table.
        /// </summary>
        private void Swarm_PeerRemoved(object sender, Peer e)
        {
            RoutingTable.Remove(e);
        }

        /// <inheritdoc />
        public async Task<Peer?> FindPeerAsync(MultiHash id, CancellationToken cancel = default)
        {
            // Can always find self.
            if (Swarm.LocalPeer.Id == id)
                return Swarm.LocalPeer;

            // Maybe the swarm knows about it.
            var found = Swarm.KnownPeers.FirstOrDefault(p => p.Id == id);
            if (found?.Addresses.Count() > 0)
                return found;

            var queryMessage = new DhtMessage()
            {
                Type = MessageType.FindNode,
                Key = id.ToArray()
            };

            // Ask our peers for information on the requested peer.
            var dquery = new DistributedQuery
            {
                Dht = this,
                AnswersNeeded = 1
            };

            await foreach (var dhtMessage in dquery.RunAsync(id, queryMessage, cancel).ConfigureAwait(false))
            {
                foreach (var peer in ProcessQueryResponseCloserPeers(id, dhtMessage.CloserPeers))
                {
                    return peer;
                }
            }
            // If not found, return the closest peer.
            return RoutingTable.NearestPeers(id).FirstOrDefault();
        }

        private IEnumerable<Peer> ProcessQueryResponseCloserPeers(MultiHash idToBeFound, DhtPeerMessage[] closerPeers)
        {
            if (closerPeers == null)
                yield break;
            foreach (var closer in closerPeers)
            {
                if (closer.TryToPeer(out Peer p))
                {
                    if (p == Swarm.LocalPeer || !Swarm.IsAllowed(p))
                        continue;

                    p = Swarm.RegisterPeer(p);
                    if (idToBeFound == p.Id)
                    {
                        yield return p;
                    }
                }
            }
        }

        /// <inheritdoc />
        public Task ProvideAsync(Cid cid, bool advertise = true, CancellationToken cancel = default)
        {
            ContentRouter.Add(cid, this.Swarm.LocalPeer.Id);
            if (advertise)
            {
                Advertise(cid.Hash, () =>
                new DhtMessage
                {
                    Type = MessageType.AddProvider,
                    Key = cid.Hash.ToArray(),
                    ProviderPeers = new DhtPeerMessage[]
                    {
                        new DhtPeerMessage
                        {
                            Id = Swarm.LocalPeer.Id.ToArray(),
                            Addresses = Swarm.LocalPeer.Addresses
                                .Select(a => a.WithoutPeerId().ToArray())
                                .ToArray()
                        }
                    }
                });
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<Peer> FindProvidersAsync(
            Cid id,
            int limit = 20,
            CancellationToken cancel = default)
        {
            var alreadySent = 0;

            // Add any providers that we already know about.
            var providers = ContentRouter
                .Get(id)
                .Select(pid =>
                {
                    return (pid == Swarm.LocalPeer.Id)
                        ? Swarm.LocalPeer
                        : Swarm.RegisterPeer(new Peer { Id = pid });
                });

            foreach (var provider in providers)
            {
                if (provider != default)
                {
                    alreadySent++;
                    yield return provider;
                }
            }

            if (limit > alreadySent)
            {
                // Ask our peers for more providers.
                var message = new DhtMessage()
                {
                    Key = id.Hash.ToArray(),
                    Type = MessageType.GetProviders
                };
                var dquery = new DistributedQuery
                {
                    Dht = this,
                    AnswersNeeded = limit,
                };
                await foreach (var dhtMessage in dquery.RunAsync(id.Hash, message, cancel).ConfigureAwait(false))
                {
                    if (limit <= alreadySent)
                        break;

                    foreach (var peer in ProcessQueryResponseProviderPeers(dhtMessage.ProviderPeers))
                    {
                        alreadySent++;
                        yield return peer;
                    }
                }
            }
        }

        private IEnumerable<Peer> ProcessQueryResponseProviderPeers(DhtPeerMessage[]? providerPeers)
        {
            if (providerPeers == null)
                yield break;

            foreach (var provider in providerPeers)
            {
                if (provider.TryToPeer(out Peer p))
                {
                    if (p == Swarm.LocalPeer || !Swarm.IsAllowed(p))
                        continue;

                    p = Swarm.RegisterPeer(p);
                    yield return p;
                }
            }
        }

        /// <summary>
        ///   Advertise that we can provide the CID to the X closest peers
        ///   of the CID.
        /// </summary>
        /// <param name="cid">
        ///   The CID to advertise.
        /// </param>
        /// <remarks>
        ///   This starts a background process to send the AddProvider message
        ///   to the 4 closest peers to the <paramref name="cid"/>.
        /// </remarks>
        public void Advertise(MultiHash targetSpace, Func<DhtMessage> messageFactory)
        {
            _ = Task.Run(async () =>
            {
                int advertsNeeded = 4;

                var peers = RoutingTable
                    .NearestPeers(targetSpace)
                    .Where(p => p != Swarm.LocalPeer);
                foreach (var peer in peers)
                {
                    try
                    {
                        using (var stream = await Swarm.DialAsync(peer, this.ToString()))
                        {
                            ProtoBuf.Serializer.SerializeWithLengthPrefix(stream, messageFactory(), PrefixStyle.Base128);
                            await stream.FlushAsync();
                        }
                        if (--advertsNeeded == 0)
                            break;
                    }
                    catch
                    {
                        // eat it.  This is fire and forget.
                    }
                }
            });
        }

        /// <summary>
        ///   Process a ping request.
        /// </summary>
        /// <remarks>
        ///   Simply return the <paramref name="request"/>.
        /// </remarks>
        private DhtMessage ProcessPing(DhtMessage request, DhtMessage response)
        {
            return request;
        }

        /// <summary>
        ///   Process a find node request.
        /// </summary>
        public DhtMessage ProcessFindNode(DhtMessage request, DhtMessage response)
        {
            // Some random walkers generate a random Key that is not hashed.
            MultiHash peerId;
            try
            {
                peerId = new MultiHash(request.Key);
            }
            catch (Exception)
            {
                log.Error($"Bad FindNode request key {request.Key.ToHexString()}");
                peerId = MultiHash.ComputeHash(request.Key);
            }

            // Do we know the peer?.
            Peer found = null;
            if (Swarm.LocalPeer.Id == peerId)
            {
                found = Swarm.LocalPeer;
            }
            else
            {
                found = Swarm.KnownPeers.FirstOrDefault(p => p.Id == peerId);
            }

            // Find the closer peers.
            var closerPeers = new List<Peer>();
            if (found != null)
            {
                closerPeers.Add(found);
            }
            else
            {
                closerPeers.AddRange(RoutingTable.NearestPeers(peerId).Take(CloserPeerCount));
            }

            // Build the response.
            response.CloserPeers = closerPeers
                .Select(peer => new DhtPeerMessage
                {
                    Id = peer.Id.ToArray(),
                    Addresses = peer.Addresses.Select(a => a.WithoutPeerId().ToArray()).ToArray()
                })
                .ToArray();

            if (log.IsDebugEnabled)
                log.Debug($"returning {response.CloserPeers.Length} closer peers");
            return response;
        }

        /// <summary>
        ///   Process a get provider request.
        /// </summary>
        public DhtMessage ProcessGetProviders(DhtMessage request, DhtMessage response)
        {
            // Find providers for the content.
            var cid = new Cid { Hash = new MultiHash(request.Key) };
            response.ProviderPeers = ContentRouter
                .Get(cid)
                .Select(pid =>
                {
                    var peer = (pid == Swarm.LocalPeer.Id)
                         ? Swarm.LocalPeer
                         : Swarm.RegisterPeer(new Peer { Id = pid });
                    return new DhtPeerMessage
                    {
                        Id = peer.Id.ToArray(),
                        Addresses = peer.Addresses.Select(a => a.WithoutPeerId().ToArray()).ToArray()
                    };
                })
                .Take(21)
                .ToArray();

            // Also return the closest peers
            return ProcessFindNode(request, response);
        }

        /// <summary>
        ///   Process an add provider request.
        /// </summary>
        public DhtMessage ProcessAddProvider(Peer remotePeer, DhtMessage request, DhtMessage response)
        {
            if (request.ProviderPeers == null)
            {
                return null;
            }
            Cid cid;
            try
            {
                cid = new Cid { Hash = new MultiHash(request.Key) };
            }
            catch (Exception)
            {
                log.Error($"Bad AddProvider request key {request.Key.ToHexString()}");
                return null;
            }
            var providers = request.ProviderPeers
                .Select(p => p.TryToPeer(out Peer peer) ? peer : (Peer)null)
                .Where(p => p != null && p == remotePeer && p.Addresses.Any() && Swarm.IsAllowed(p));
            foreach (var provider in providers)
            {
                Swarm.RegisterPeer(provider);
                ContentRouter.Add(cid, provider.Id);
            }

            // There is no response for this request.
            return null;
        }
        /// <summary>
        ///   Process an put value request.
        /// </summary>
        private DhtMessage ProcessFindSimilarValues(Peer remotePeer, DhtMessage request, DhtMessage response)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        ///   Process an put value request.
        /// </summary>
        private async Task<DhtMessage?> ProcessGetValueAsync(Peer remotePeer, DhtMessage request, DhtMessage response)
        {
            if (request.Key == null || request.Record == null || request.Record.Key == null)
            {
                return null;
            }

            Store.SetNamespace($"dht-values.{request.Key.ToBase32()}");

            var val = await Store.TryGetAsync(new MultiHash(request.Record.Key));

            if (val == default)
                return null;

            response.Key = request.Key;
            response.Record.Key = request.Key;
            response.Record.Value = val;

            // Also return the closest peers
            return ProcessFindNode(request, response);
        }

        /// <summary>
        ///   Process an put value request.
        /// </summary>
        private async Task<DhtMessage?> ProcessPutValue(Peer remotePeer, DhtMessage request, DhtMessage response)
        {
            if (request.Key == null || request.Record == null || request.Record.Key == null)
            {
                return null;
            }

            Store.SetNamespace($"dht-values.{request.Key.ToBase32()}");

            await Store.PutAsync(new MultiHash(request.Record.Key), request.Record.Value);

            // There is no response for this request.
            return null;
        }

        public Task<IEnumerable<byte[]>> FindSimilarValuesAsync(string @namespace, MultiHash key, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task<byte[]?> TryGetValueAsync(string @namespace, MultiHash key, CancellationToken cancellationToken)
        {
            Store.SetNamespace(@namespace);

            var value = await Store.TryGetAsync(key);

            if (value == default)
            {
                // Ask our peers for more providers.
                var message = new DhtMessage()
                {
                    Type = MessageType.GetValue,
                    Key = @namespace.FromBase32(),
                    Record = new DhtRecordMessage()
                    {
                        Key = key.ToArray()
                    }
                };
                var dquery = new DistributedQuery
                {
                    Dht = this,
                    AnswersNeeded = 1,
                };
                await foreach (var dhtMessage in dquery.RunAsync(key, message, cancellationToken).ConfigureAwait(false))
                {
                    value = dhtMessage.Record.Value;
                }
            }

            return value;
        }

        public async Task PutValueAsync(string @namespace, MultiHash key, byte[] value, CancellationToken cancellationToken = default)
        {
            Store.SetNamespace(@namespace);
            await Store.PutAsync(key, value);
            var advertise = true;//maybe make parametric in future
            if (advertise)
            {
                Advertise(key, () =>
                new DhtMessage
                {
                    Type = MessageType.PutValue,
                    Key = @namespace.FromBase32(),
                    Record = new DhtRecordMessage()
                    {
                        Key = key.ToArray(),
                        Value = value
                    }
                }
                );
            }
        }
    }
}