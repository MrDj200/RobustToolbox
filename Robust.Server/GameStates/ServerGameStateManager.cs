﻿using System.Collections.Generic;
using Robust.Server.Interfaces.GameObjects;
using Robust.Server.Interfaces.GameState;
using Robust.Server.Interfaces.Player;
using Robust.Shared.Enums;
using Robust.Shared.GameStates;
using Robust.Shared.Interfaces.Map;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Interfaces.Timing;
using Robust.Shared.IoC;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Server.GameStates
{
    public class ServerGameStateManager : IServerGameStateManager
    {
        // Mapping of net UID of clients -> last known acked state.
        private readonly Dictionary<long, GameTick> ackedStates = new Dictionary<long, GameTick>();

        private GameTick lastOldestAck = GameTick.Zero;

        [Dependency]
        private IServerEntityManager _entityManager;

        [Dependency]
        private IGameTiming _gameTiming;

        [Dependency]
        private IServerNetManager _networkManager;

        [Dependency]
        private IPlayerManager _playerManager;

        [Dependency] private IMapManager _mapManager;

        public void Initialize()
        {
            _networkManager.RegisterNetMessage<MsgState>(MsgState.NAME);
            _networkManager.RegisterNetMessage<MsgStateAck>(MsgStateAck.NAME, HandleStateAck);

            _networkManager.Disconnect += HandleClientDisconnect;
        }

        private void HandleClientDisconnect(object sender, NetChannelArgs e)
        {
            if (ackedStates.ContainsKey(e.Channel.ConnectionId))
                ackedStates.Remove(e.Channel.ConnectionId);
        }

        private void Ack(long uniqueIdentifier, GameTick stateAcked)
        {
            if(ackedStates.ContainsKey(uniqueIdentifier))
                ackedStates[uniqueIdentifier] = stateAcked;
        }

        public void SendGameStateUpdate()
        {
            DebugTools.Assert(_networkManager.IsServer);

            if (!_networkManager.IsConnected)
            {
                // Prevent deletions piling up if we have no clients.
                _entityManager.CullDeletionHistory(GameTick.MaxValue);
                _mapManager.CullDeletionHistory(GameTick.MaxValue);
                return;
            }

            var oldestAck = GameTick.MaxValue;
            foreach (var connection in _networkManager.Channels)
            {
                if (!ackedStates.TryGetValue(connection.ConnectionId, out var ack))
                {
                    ackedStates.Add(connection.ConnectionId, GameTick.Zero);
                }
                else if (ack < oldestAck)
                {
                    oldestAck = ack;
                }
            }

            // If there are no clients FULLY connected to the server (no channels),
            // or server has been running for 2.2 years
            if (oldestAck == GameTick.MaxValue)
            {
                return; // don't need to worry about states.
            }

            if (oldestAck > lastOldestAck)
            {
                lastOldestAck = oldestAck;
                _entityManager.CullDeletionHistory(oldestAck);
            }

            var entities = _entityManager.GetEntityStates(oldestAck);
            var players = _playerManager.GetPlayerStates(oldestAck);
            var deletions = _entityManager.GetDeletedEntities(oldestAck);
            var mapData = _mapManager.GetStateData(oldestAck);

            var state = new GameState(oldestAck, _gameTiming.CurTick, entities, players, deletions, mapData);

            foreach (var c in _networkManager.Channels)
            {
                var session = _playerManager.GetSessionByChannel(c);

                if (session == null || session.Status != SessionStatus.InGame)
                    continue;

                var stateUpdateMessage = _networkManager.CreateNetMessage<MsgState>();
                stateUpdateMessage.State = state;
                _networkManager.ServerSendMessage(stateUpdateMessage, c);
            }
        }

        private void HandleStateAck(MsgStateAck msg)
        {
            Ack(msg.MsgChannel.ConnectionId, msg.Sequence);
        }
    }
}