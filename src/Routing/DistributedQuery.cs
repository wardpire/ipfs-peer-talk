using Common.Logging;
using Ipfs;
using ProtoBuf;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PeerTalk.Routing
{
    /// <summary>
    ///   A query that is sent to multiple peers.
    /// </summary>
    /// <typeparam name="T">
    ///  The type of answer returned by a peer.
    /// </typeparam>
    public class DistributedQuery
    {
        private static readonly ILog log = LogManager.GetLogger("PeerTalk.Routing.DistributedQuery");
        private static int nextQueryId = 1;

        /// <summary>
        ///   The maximum number of peers that can be queried at one time
        ///   for all distributed queries.
        /// </summary>
        private static readonly SemaphoreSlim askCount = new(128);

        /// <summary>
        ///   The maximum time spent on waiting for an answer from a peer.
        /// </summary>
        private static readonly TimeSpan askTime = TimeSpan.FromSeconds(10);

        /// <summary>
        ///   Controls the running of the distributed query.
        /// </summary>
        /// <remarks>
        ///   Becomes cancelled when the correct number of answers are found
        ///   or the caller of <see cref="RunAsync"/> wants to cancel
        ///   or the DHT is stopped.
        /// </remarks>
        private CancellationTokenSource runningQuery;

        private readonly ConcurrentDictionary<Peer, Peer> visited = new();
        private readonly ConcurrentDictionary<DhtMessage, DhtMessage> answers = new();
        private int failedConnects = 0;

        /// <summary>
        ///   The unique identifier of the query.
        /// </summary>
        public int Id { get; } = nextQueryId++;

        /// <summary>
        ///   The received answers for the query.
        /// </summary>
        public IEnumerable<DhtMessage> Answers
        {
            get
            {
                return answers.Values;
            }
        }

        /// <summary>
        ///   The number of answers needed.
        /// </summary>
        /// <remarks>
        ///   When the numbers <see cref="Answers"/> reaches this limit
        ///   the <see cref="RunAsync">running query</see> will stop.
        /// </remarks>
        public int AnswersNeeded { get; set; } = 1;

        /// <summary>
        ///   The maximum number of concurrent peer queries to perform
        ///   for one distributed query.
        /// </summary>
        /// <value>
        ///   The default is 16.
        /// </value>
        /// <remarks>
        ///   The number of peers that are asked for the answer.
        /// </remarks>
        public int ConcurrencyLevel { get; set; } = 16;

        /// <summary>
        ///   The distributed hash table.
        /// </summary>
        public Dht1 Dht { get; set; }

        /// <summary>
        ///   Starts the distributed query.
        /// </summary>
        /// <param name="cancel">
        ///   Is used to stop the task.  When cancelled, the <see cref="TaskCanceledException"/> is raised.
        /// </param>
        /// <returns>
        ///   A task that represents the asynchronous operation.
        /// </returns>
        public async IAsyncEnumerable<DhtMessage> RunAsync(MultiHash targetKey, DhtMessage queryMessage, CancellationToken cancel)
        {
            log.Debug($"Q{Id} run {queryMessage.Key} {targetKey}");

            runningQuery = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            Dht.Stopped += OnDhtStopped;

            var tasks = Enumerable
                .Range(1, ConcurrencyLevel)
                .Select(id => AskAsync(id, targetKey, queryMessage));
            try
            {
                await foreach (var result in tasks.Merge().WithCancellation(cancel))
                    yield return result;
            }
            finally
            {
                Dht.Stopped -= OnDhtStopped;
            }
            log.Debug($"Q{Id} found {answers.Count} answers, visited {visited.Count} peers, failed {failedConnects}");
        }

        private void OnDhtStopped(object sender, EventArgs e)
        {
            log.Debug($"Q{Id} cancelled because DHT stopped.");
            runningQuery.Cancel();
        }

        /// <summary>
        ///   Ask the next peer the question.
        /// </summary>
        private async IAsyncEnumerable<DhtMessage> AskAsync(int taskId, MultiHash targetKey, DhtMessage queryMessage)
        {
            int pass = 0;
            int waits = 20;
            while (!runningQuery.IsCancellationRequested && waits > 0)
            {
                // Get the nearest peer that has not been visited.
                var peer = Dht.RoutingTable
                    .NearestPeers(targetKey)
                    .FirstOrDefault(p => !visited.ContainsKey(p));
                if (peer == null)
                {
                    --waits;
                    await Task.Delay(128);
                    continue;
                }

                if (!visited.TryAdd(peer, peer))
                {
                    continue;
                }
                ++pass;

                // Ask the nearest peer.
                await askCount.WaitAsync(runningQuery.Token).ConfigureAwait(false);
                var start = DateTime.Now;
                log.Debug($"Q{Id}.{taskId}.{pass} ask {peer}");
                DhtMessage? response = default;
                try
                {
                    using (var timeout = new CancellationTokenSource(askTime))
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, runningQuery.Token))
                    using (var stream = await Dht.Swarm.DialAsync(peer, Dht.ToString(), cts.Token).ConfigureAwait(false))
                    {
                        // Send the KAD query and get a response.
                        ProtoBuf.Serializer.SerializeWithLengthPrefix(stream, queryMessage, PrefixStyle.Base128);
                        await stream.FlushAsync(cts.Token).ConfigureAwait(false);
                        response = await ProtoBufHelper.ReadMessageAsync<DhtMessage>(stream, cts.Token).ConfigureAwait(false);
                    }
                    var time = DateTime.Now - start;
                    log.Debug($"Q{Id}.{taskId}.{pass} ok {peer} ({time.TotalMilliseconds} ms)");
                }
                catch (Exception e)
                {
                    response = default;
                    Interlocked.Increment(ref failedConnects);
                    var time = DateTime.Now - start;
                    log.Warn($"Q{Id}.{taskId}.{pass} failed ({time.TotalMilliseconds} ms) - {e.Message}");
                    // eat it
                }
                finally
                {
                    askCount.Release();
                }
                if (response != default)
                {
                    if (answers.TryAdd(response, response))
                        yield return response;
                }
                if (answers.Count >= AnswersNeeded && runningQuery?.IsCancellationRequested == false)
                    await runningQuery.CancelAsync();
            }
        }
    }
}