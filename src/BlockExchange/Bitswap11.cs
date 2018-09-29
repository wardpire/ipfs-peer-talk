﻿using Common.Logging;
using Ipfs;
using PeerTalk.Protocols;
using ProtoBuf;
using Semver;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PeerTalk.BlockExchange
{
    /// <summary>
    ///   Identifies the peer.
    /// </summary>
    public class Bitswap11 : IPeerProtocol
    {
        static ILog log = LogManager.GetLogger(typeof(Bitswap11));

        /// <inheritdoc />
        public string Name { get; } = "ipfs/bitswap";

        /// <inheritdoc />
        public SemVersion Version { get; } = new SemVersion(1, 1);

        /// <inheritdoc />
        public override string ToString()
        {
            return $"/{Name}/{Version}";
        }

        /// <inheritdoc />
        public async Task ProcessMessageAsync(PeerConnection connection, Stream stream, CancellationToken cancel = default(CancellationToken))
        {
            var request = await ProtoBufHelper.ReadMessageAsync<Message>(stream, cancel);

            // TODO: Process want list
            foreach (var entry in request.wantlist.entries)
            {
                var s = Base58.ToBase58(entry.block);
                Cid cid = s;
                log.Debug($"{connection.RemoteAddress} wants {cid}");
            }
            // TODO: Process sent blocks
        }

        [ProtoContract]
        class Entry
        {
            [ProtoMember(1)]
            // changed from string to bytes, it makes a difference in JavaScript
            public byte[] block;      // the block cid (cidV0 in bitswap 1.0.0, cidV1 in bitswap 1.1.0)

            [ProtoMember(2)]
            public int priority = 1;    // the priority (normalized). default to 1

            [ProtoMember(3)]
            public bool cancel;       // whether this revokes an entry
        }

        [ProtoContract]
        class Wantlist
        {
            [ProtoMember(1)]
            public Entry[] entries;       // a list of wantlist entries

            [ProtoMember(2)]
            public bool full;           // whether this is the full wa2tlist. default to false
        }

        [ProtoContract]
        class Block
        {
            [ProtoMember(1)]
            public byte[] prefix;        // CID prefix (cid version, multicodec and multihash prefix (type + length)
            [ProtoMember(2)]
            byte[] data;
        }

        [ProtoContract]
        class Message
        {
            [ProtoMember(1)]
            public Wantlist wantlist;

            [ProtoMember(2)]
            public byte[][] blocks;          // used to send Blocks in bitswap 1.0.0

            [ProtoMember(3)]
            public List<Block> payload;         // used to send Blocks in bitswap 1.1.0
        }

    }
}