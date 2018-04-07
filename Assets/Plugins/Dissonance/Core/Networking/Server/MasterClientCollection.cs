using System;
using System.Collections.Generic;
using System.Linq;
using Dissonance.Audio.Codecs;

namespace Dissonance.Networking.Server
{
    /// <summary>
    /// A client collection which assigns peer IDs and broadcasts all state changes
    /// </summary>
    /// <typeparam name="TPeer"></typeparam>
    internal class BroadcastingClientCollection<TPeer>
        : BaseClientCollection<TPeer>
    {
        #region fields and properties
        private readonly IServer<TPeer> _server;

        private readonly byte[] _tmpSendBuffer = new byte[1024];
        private readonly List<TPeer> _tmpConnectionBuffer = new List<TPeer>();
        private readonly List<ClientInfo<TPeer>> _tmpClientBuffer = new List<ClientInfo<TPeer>>();
        private readonly List<ClientInfo<TPeer>> _tmpClientBufferHandshake = new List<ClientInfo<TPeer>>();
        #endregion

        #region constructor
        public BroadcastingClientCollection(IServer<TPeer> server)
        {
            _server = server;
        }
        #endregion

        protected override void OnRemovedClient(ClientInfo<TPeer> client)
        {
            base.OnRemovedClient(client);

            //Write the removal message
            var writer = new PacketWriter(_tmpSendBuffer);
            writer.WriteRemoveClient(_server.SessionId, client.PlayerId);

            //Broadcast to all peers
            Broadcast(writer.Written);
        }

        protected override void OnAddedClient(ClientInfo<TPeer> client)
        {
            base.OnAddedClient(client);

            _server.AddClient(client);
        }

        #region packet processing
        public void ProcessHandshakeRequest(TPeer source, ref PacketReader reader)
        {
            //Parse packet
            string name;
            CodecSettings codecSettings;
            reader.ReadHandshakeRequest(out name, out codecSettings);

            // Validate that we have a player name, and ignore if not
            if (name == null)
            {
                Log.Warn("Ignoring a handshake with a null player name");
                return;
            }

            // Check if this client is already in the session but with a different connection to the current one.
            // We'll assume name collisions never happen, so this is probably a client reconnecting before the server has cleaned up after a very recent disconnection
            ClientInfo<TPeer> currentInfoByName;
            ClientInfo<TPeer> currentInfoByConn;
            if (TryGetClientInfoByName(name, out currentInfoByName) | TryFindClientByConnection(source, out currentInfoByConn))
            {
                //We got the client by name and by current connection. If they are different then the client is in the session with a different connection
                if (!EqualityComparer<ClientInfo<TPeer>>.Default.Equals(currentInfoByName, currentInfoByConn))
                {
                    //Remove clients who were already connected
                    if (currentInfoByConn != null && currentInfoByConn.IsConnected)
                        RemoveClient(currentInfoByConn);
                    if (currentInfoByName != null && currentInfoByName.IsConnected)
                        RemoveClient(currentInfoByName);

                    Log.Debug("Client '{0}' handshake received but client is already connected! Disconnecting client '{1}' & '{2}', connecting '{3}'",
                        name,
                        currentInfoByConn,
                        currentInfoByName,
                        source
                    );
                }
            }

            //Get or register the ID for this client
            var id = PlayerIds.GetId(name) ?? PlayerIds.Register(name);
            var info = GetOrCreateClientInfo(id, name, codecSettings, source);

            //Send back handshake response
            _tmpClientBufferHandshake.Clear();
            GetClients(_tmpClientBufferHandshake);

            var writer = new PacketWriter(_tmpSendBuffer);
            writer.WriteHandshakeResponse(_server.SessionId, info.PlayerId, _tmpClientBufferHandshake, ClientsInRooms.ByName);
            _server.SendReliable(source, writer.Written);
        }

        public override void ProcessClientState(TPeer source, ref PacketReader reader)
        {
            //Rebroadcast packet to all peers so they can update their state
            Broadcast(reader.All);

            base.ProcessClientState(source, ref reader);
        }

        public override void ProcessDeltaChannelState(ref PacketReader reader)
        {
            //Rebroadcast packet to all peers so they can update their state
            Broadcast(reader.All);

            base.ProcessDeltaChannelState(ref reader);
        }
        #endregion

        private void Broadcast(ArraySegment<byte> packet)
        {
            _tmpConnectionBuffer.Clear();
            _tmpClientBuffer.Clear();

            //Get all client infos
            GetClients(_tmpClientBuffer);

            //Now get all connections (except the one excluded one, if applicable)
            for (var i = 0; i < _tmpClientBuffer.Count; i++)
            {
                var c = _tmpClientBuffer[i];
                _tmpConnectionBuffer.Add(c.Connection);
            }

            //Broadcast to all those connections
            _server.SendReliable(_tmpConnectionBuffer, packet);

            _tmpConnectionBuffer.Clear();
            _tmpClientBuffer.Clear();
        }

        public void RemoveClient(TPeer connection)
        {
            ClientInfo<TPeer> info;
            if (TryFindClientByConnection(connection, out info))
                RemoveClient(info);
        }
    }
}
