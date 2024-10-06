using ProtoBuf;

namespace PeerTalk.Routing
{
    public class KeyValueBytesPair
    {
        /// <summary>
        ///   TODO
        /// </summary>
        [ProtoMember(1)]
        public byte[] Key { get; set; }

        /// <summary>
        ///   TODO
        /// </summary>
        [ProtoMember(2)]
        public byte[] Value { get; set; }
    }
}