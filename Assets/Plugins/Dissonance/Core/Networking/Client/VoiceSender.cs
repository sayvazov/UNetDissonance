using System;
using System.Collections.Generic;
using Dissonance.Threading;

namespace Dissonance.Networking.Client
{
    internal class VoiceSender<TPeer>
        where TPeer : struct
    {
        #region fields and properties
        private static readonly Log Log = Logs.Create(LogCategory.Network, typeof(VoiceSender<TPeer>).Name);

        private readonly ISendQueue<TPeer> _sender;
        private readonly ISession _session;
        private readonly IClientCollection<TPeer?> _peers;
        private readonly EventQueue _events;
        private readonly PlayerChannels _playerChannels;
        private readonly RoomChannels _roomChannels;

        private byte _channelSessionId;
        private readonly ReadonlyLockedValue<List<OpenChannel>> _openChannels = new ReadonlyLockedValue<List<OpenChannel>>(new List<OpenChannel>());

        private readonly List<KeyValuePair<string, ChannelProperties>> _pendingPlayerChannels = new List<KeyValuePair<string, ChannelProperties>>();

        private ushort _sequenceNumber;

        private readonly HashSet<ClientInfo<TPeer?>> _tmpDestsSet = new HashSet<ClientInfo<TPeer?>>();
        private readonly List<ClientInfo<TPeer?>> _tmpDestsList = new List<ClientInfo<TPeer?>>();

        private bool _hadId;
        private int _noIdSendCount;
        #endregion

        #region constructor
        public VoiceSender(
            [NotNull] ISendQueue<TPeer> sender,
            [NotNull] ISession session,
            [NotNull] IClientCollection<TPeer?> peers,
            [NotNull] EventQueue events,
            [NotNull] PlayerChannels playerChannels,
            [NotNull] RoomChannels roomChannels)
        {
            if (sender == null) throw new ArgumentNullException("sender");
            if (session == null) throw new ArgumentNullException("session");
            if (peers == null) throw new ArgumentNullException("peers");
            if (events == null) throw new ArgumentNullException("events");
            if (playerChannels == null) throw new ArgumentNullException("playerChannels");
            if (roomChannels == null) throw new ArgumentNullException("roomChannels");

            _sender = sender;
            _session = session;
            _peers = peers;
            _playerChannels = playerChannels;
            _roomChannels = roomChannels;
            _events = events;

            _playerChannels.OpenedChannel += OpenPlayerChannel;
            _playerChannels.ClosedChannel += ClosePlayerChannel;
            _roomChannels.OpenedChannel += OpenRoomChannel;
            _roomChannels.ClosedChannel += CloseRoomChannel;

            //There may already be some channels which were created before we created those events, run through them all now so we're up to date
            foreach (var playerChannel in playerChannels)
                OpenPlayerChannel(playerChannel.Value.TargetId, playerChannel.Value.Properties);
            foreach (var roomChannel in roomChannels)
                OpenRoomChannel(roomChannel.Value.TargetId, roomChannel.Value.Properties);

            //We need to watch for player join/leave events to properly handle player channel management
            _events.PlayerJoined += OnPlayerJoined;
            _events.PlayerLeft += OnPlayerLeft;
        }
        #endregion

        public void Stop()
        {
            //Stop listening for channel events
            _playerChannels.OpenedChannel -= OpenPlayerChannel;
            _playerChannels.ClosedChannel -= ClosePlayerChannel;
            _roomChannels.OpenedChannel -= OpenRoomChannel;
            _roomChannels.ClosedChannel -= CloseRoomChannel;

            //Stop listening for player events
            _events.PlayerJoined -= OnPlayerJoined;
            _events.PlayerLeft -= OnPlayerLeft;

            //Discard all open channels
            using (var openChannels = _openChannels.Lock())
                openChannels.Value.Clear();
        }

        #region sending channel management
        private void OnPlayerJoined([NotNull] string name, CodecSettings codecSettings)
        {
            if (name == null) throw new ArgumentNullException("name");

            //Find any pending channels which address this new player by name and open them now we know who they are
            for (var i = _pendingPlayerChannels.Count - 1; i >= 0; i--)
            {
                var p = _pendingPlayerChannels[i];
                if (p.Key == name)
                {
                    OpenPlayerChannel(p.Key, p.Value);
                    _pendingPlayerChannels.RemoveAt(i);
                }
            }
        }

        private void OnPlayerLeft([NotNull] string name)
        {
            if (name == null) throw new ArgumentNullException("name");

            using (var unlocker = _openChannels.Lock())
            {
                var openChannels = unlocker.Value;

                //Find any currently open channels to this player and revert them back to pending channels
                for (var i = openChannels.Count - 1; i >= 0; i--)
                {
                    var c = openChannels[i];

                    if (c.Type == ChannelType.Player && c.Name == name)
                    {
                        //Remove it, no need to set it to closing because the only person who cares about this channel just disconnected
                        openChannels.RemoveAt(i);

                        //If it wasn't closing then we need to store this channel in case a player with the same name (re)connects
                        if (!c.IsClosing)
                            _pendingPlayerChannels.Add(new KeyValuePair<string, ChannelProperties>(c.Name, c.Config));
                    }
                }
            }
        }

        private void OpenPlayerChannel([NotNull] string player, [NotNull] ChannelProperties config)
        {
            if (player == null) throw new ArgumentNullException("player");
            if (config == null) throw new ArgumentNullException("config");

            //If we don't know who this player is yet save the channel open event and process it later
            ClientInfo<TPeer?> info;
            if (!_peers.TryGetClientInfoByName(player, out info))
            {
                _pendingPlayerChannels.Add(new KeyValuePair<string, ChannelProperties>(player, config));
                return;
            }

            OpenChannel(ChannelType.Player, config, info.PlayerId, info.PlayerName);
        }

        private void ClosePlayerChannel([NotNull] string player, [NotNull] ChannelProperties config)
        {
            if (player == null) throw new ArgumentNullException("player");
            if (config == null) throw new ArgumentNullException("config");

            //Remove from the set of unlinked player channels
            for (var i = _pendingPlayerChannels.Count - 1; i >= 0; i--)
                if (_pendingPlayerChannels[i].Key == player && ReferenceEquals(config, _pendingPlayerChannels[i].Value))
                    _pendingPlayerChannels.RemoveAt(i);

            //Try to find the player - we may not find them (they may not have even joined the session yet)
            ClientInfo<TPeer?> info;
            if (!_peers.TryGetClientInfoByName(player, out info))
                return;

            CloseChannel(ChannelType.Player, config, info.PlayerId);
        }

        private void OpenRoomChannel([NotNull] string room, [NotNull] ChannelProperties config)
        {
            if (room == null) throw new ArgumentNullException("room");
            if (config == null) throw new ArgumentNullException("config");

            OpenChannel(ChannelType.Room, config, room.ToRoomId(), room);
        }

        private void CloseRoomChannel([NotNull] string room, [NotNull] ChannelProperties config)
        {
            if (room == null) throw new ArgumentNullException("room");
            if (config == null) throw new ArgumentNullException("config");

            CloseChannel(ChannelType.Room, config, room.ToRoomId());
        }

        private void OpenChannel(ChannelType type, [NotNull] ChannelProperties config, ushort recipient, [NotNull] string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (config == null) throw new ArgumentNullException("config");

            using (var unlocker = _openChannels.Lock())
            {
                var openChannels = unlocker.Value;

                var allWereClosed = true;
                int index;

                //Check if we have a closing channel which we're now trying to re-open
                var reopened = false;
                for (index = 0; index < openChannels.Count; index++)
                {
                    var c = openChannels[index];

                    if (!c.IsClosing)
                        allWereClosed = false;

                    if (c.Type == type && ReferenceEquals(c.Config, config) && c.Recipient == recipient)
                    {
                        openChannels[index] = c.AsOpen();
                        reopened = true;
                        break;
                    }
                }

                //Finish off accumulating the `allWereClosed` flag
                for (index++; index < openChannels.Count && allWereClosed; index++)
                    allWereClosed &= openChannels[index].IsClosing;

                //Failed to find a channel to re-open so just add a new one
                if (!reopened)
                    openChannels.Add(new OpenChannel(type, 0, config, false, recipient, name));

                //All channels were closing, bump up the voice session ID so the receiving end can tell that the channels closed and re-opened
                if (allWereClosed)
                    unchecked { _channelSessionId++; }
            }
        }

        private void CloseChannel(ChannelType type, [NotNull] ChannelProperties properties, ushort id)
        {
            if (properties == null) throw new ArgumentNullException("properties");

            using (var unlocker = _openChannels.Lock())
            {
                var openChannels = unlocker.Value;

                //Find the channel and change it to a closing version of the channel
                for (var index = 0; index < openChannels.Count; index++)
                {
                    var channel = openChannels[index];
                    if (!channel.IsClosing && channel.Type == type && channel.Recipient == id && ReferenceEquals(channel.Config, properties))
                        openChannels[index] = channel.AsClosing();
                }
            }
        }

        private void ClearClosedChannels()
        {
            //Remove all channels which are closing (now that they've been included in a packet)
            using (var unlocker = _openChannels.Lock())
            {
                var openChannels = unlocker.Value;

                for (var i = openChannels.Count - 1; i >= 0; i--)
                    if (openChannels[i].IsClosing)
                        openChannels.RemoveAt(i);
            }
        }
        #endregion

        public void Send(ArraySegment<byte> encodedAudio)
        {
            if (encodedAudio.Array == null) throw new ArgumentNullException("encodedAudio");

            //Sanity check (cannot send before assigned an ID)
            if (!_session.LocalId.HasValue)
            {
                //ncrunch: no coverage start (Sanity check, should never happen - client ID should never be unset once it has been set)
                if (_hadId)
                {
                    Log.Warn("Attempted to send voice but client ID has been unset");
                    return;
                }
                //ncrunch: no coverage end

                //It's possible that we try to send voice before being assigned an ID (simply because we're still joining the session). Delay the notification for a short period...
                //...and also don't spam it once it does happen
                if (_noIdSendCount++ >= 99 && _noIdSendCount % 50 == 0)
                    Log.Warn("Attempted to send voice before assigned a client ID by the host ({0} packets discarded so far)", _noIdSendCount);
                else
                    Log.Debug("Attempted to send voice before assigned a client ID by the host ({0} packets discarded so far)", _noIdSendCount);

                return;
            }
            _hadId = true;
            _noIdSendCount = 0;

            using (var unlocker = _openChannels.Lock())
            {
                var openChannels = unlocker.Value;

                //Early exit (no point sending to no one)
                if (openChannels.Count == 0)
                {
                    Log.Debug("Attempted to send voice with no open channels");
                    return;
                }

                //Who is interested in this audio?
                var destinations = GetVoiceDestinations(openChannels);
                if (destinations.Count > 0)
                {
                    //Write the packet
                    var packet = new PacketWriter(_sender.SendBufferPool.Get())
                        .WriteVoiceData(_session.SessionId, _session.LocalId.Value, _sequenceNumber, _channelSessionId, openChannels, encodedAudio)
                        .Written;
                    unchecked { _sequenceNumber++; }

                    //Send packet
                    _sender.EnqueueUnreliableP2P(_session.LocalId.Value, destinations, packet);

                    //Now that the channels have been sent in a packet we can remove the closing ones from the list
                    ClearClosedChannels();
                }
                else
                    Log.Trace("Dropping voice packet (no listening destinations)");

                //Clean up
                destinations.Clear();
            }
        }

        [NotNull] private List<ClientInfo<TPeer?>> GetVoiceDestinations([NotNull] IList<OpenChannel> openChannels)
        {
            //Clear a set and a list, we'll only add to the list if we add to the set. Therefore the...
            //...list will contain the same items, but in a useful form (ultimately we want a list)
            _tmpDestsSet.Clear();
            _tmpDestsList.Clear();

            for (var i = 0; i < openChannels.Count; i++)
            {
                var chan = openChannels[i];

                if (chan.Type == ChannelType.Player)
                {
                    ClientInfo<TPeer?> info;
                    if (_peers.TryGetClientInfoById(chan.Recipient, out info)) {
                        if (_tmpDestsSet.Add(info))
                            _tmpDestsList.Add(info);
                    }
                    else
                        Log.Debug("Attempted to send voice to unknown player ID '{0}'", chan.Recipient);    //ncrunch: no coverage (Sanity check, should never happen)
                }
                else if (chan.Type == ChannelType.Room)
                {
                    List<ClientInfo<TPeer?>> roomClients;
                    if (_peers.TryGetClientsInRoom(chan.Recipient, out roomClients))
                    {
                        for (var r = 0; r < roomClients.Count; r++)
                        {
                            var c = roomClients[r];
                            if (_tmpDestsSet.Add(c))
                                _tmpDestsList.Add(c);
                        }
                    }
                }
                else
                    //ncrunch: no coverage start (Sanity check, should never happen)
                    throw Log.CreatePossibleBugException(string.Format("Attempted to send to a channel with an unknown type '{0}'", chan.Type), "CF735F3F-F954-4F05-9C5D-5153AB1E30E7");
                    //ncrunch: no coverage end
            }

            _tmpDestsSet.Clear();
            return _tmpDestsList;
        }
    }
}
