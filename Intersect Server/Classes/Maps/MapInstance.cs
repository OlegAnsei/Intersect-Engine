﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using Intersect.Enums;
using Intersect.GameObjects;
using Intersect.GameObjects.Events;
using Intersect.GameObjects.Maps;
using Intersect.Server.Classes.Database;
using Intersect.Server.Classes.Database.PlayerData.Characters;
using Intersect.Server.Classes.Entities;
using Intersect.Server.Classes.General;
using Intersect.Server.Classes.Networking;
using Newtonsoft.Json;
using EventInstance = Intersect.Server.Classes.Entities.EventInstance;

namespace Intersect.Server.Classes.Maps
{
    using LegacyDatabase = Intersect.Server.Classes.Core.LegacyDatabase;

    public class MapInstance : MapBase
    {
        private static MapInstances sLookup;

        //Does the map have a player on or nearby it?
        [JsonIgnore]
        public bool Active;

        private List<EntityInstance> mEntities = new List<EntityInstance>();

        [JsonIgnore] [NotMapped] public Dictionary<EventBase, EventInstance> GlobalEventInstances = new Dictionary<EventBase, EventInstance>();
        [JsonIgnore] public List<MapItemSpawn> ItemRespawns = new List<MapItemSpawn>();
        private Point[] mMapBlocks;

        //Location of Map in the current grid
        [JsonIgnore]
        public int MapGrid;

        [JsonIgnore]
        public int MapGridX = -1;
        [JsonIgnore]
        public int MapGridY = -1;
        [JsonIgnore]
        public List<MapItem> MapItems = new List<MapItem>();

        //Projectiles
        [JsonIgnore]
        public List<Projectile> MapProjectiles = new List<Projectile>();

        private Point[] mNpcMapBlocks;
        [JsonIgnore]
        public Dictionary<NpcSpawn, MapNpcSpawn> NpcSpawnInstances = new Dictionary<NpcSpawn, MapNpcSpawn>();
        private List<Player> mPlayers = new List<Player>();

        [JsonIgnore]
        public Dictionary<ResourceSpawn, MapResourceSpawn> ResourceSpawnInstances =
            new Dictionary<ResourceSpawn, MapResourceSpawn>();

        //Temporary Values
        [JsonIgnore]
        public List<int> SurroundingMaps = new List<int>();

        [JsonIgnore]
        public long TileAccessTime;
        [JsonIgnore]
        public long LastUpdateTime = -1;
        [JsonIgnore]
        public long UpdateDelay = 100;

        //Init
        public MapInstance() : base(-1, false)
        {
            Layers = null;
        }

        [JsonConstructor]
        public MapInstance(int index) : base(index, false)
        {
            if (index == -1)
            {
                return;
            }
            Layers = null;
        }

    public new static MapInstances Lookup => (sLookup = (sLookup ?? new MapInstances(MapBase.Lookup)));

        //GameObject Functions

        public object GetMapLock()
        {
            return mMapLock;
        }

        public override void Load(string json)
        {
            Load(json, -1);
        }

        public void Initialize()
        {
            lock (mMapLock)
            {
                CacheMapBlocks();
                RespawnEverything();
            }
        }

        public void Load(string json, int keepRevision = -1)
        {
            lock (mMapLock)
            {
                DespawnEverything();
                base.Load(json);
                if (keepRevision > -1) Revision = keepRevision;
                CacheMapBlocks();
                RespawnEverything();
            }
        }

        private void CacheMapBlocks()
        {
            var blocks = new List<Point>();
            var npcBlocks = new List<Point>();
            for (int x = 0; x < Options.MapWidth; x++)
            {
                for (int y = 0; y < Options.MapHeight; y++)
                {
                    if (Attributes[x, y] != null)
                    {
                        if (Attributes[x, y].Value == (int) MapAttributes.Blocked ||
                            Attributes[x, y].Value == (int) MapAttributes.GrappleStone)
                        {
                            blocks.Add(new Point(x, y));
                            npcBlocks.Add(new Point(x, y));
                        }
                        else if (Attributes[x, y].Value == (int) MapAttributes.NpcAvoid)
                        {
                            npcBlocks.Add(new Point(x, y));
                        }
                    }
                }
            }
            mMapBlocks = blocks.ToArray();
            mNpcMapBlocks = npcBlocks.ToArray();
        }

        public Point[] GetCachedBlocks(bool isPlayer)
        {
            if (isPlayer) return mMapBlocks;
            return mNpcMapBlocks;
        }

        //Get Map Packet
        public string GetMapPacket(bool forClient)
        {
            return JsonData;
        }

        public byte[] GetTileData(bool shouldCache = true)
        {
            //If the tile data is cached then send it
            //Else grab it (and maybe cache it)
            lock (GetMapLock())
            {
                if (TileData != null)
                {
                    if (shouldCache)
                    {
                        TileAccessTime = Globals.System.GetTimeMs();
                    }
                    return TileData;
                }
                else
                {
                    if (shouldCache)
                    {
                        TileData = LegacyDatabase.GetMapTiles(Index);
                        TileAccessTime = Globals.System.GetTimeMs();
                        return TileData;
                    }
                    else
                    {
                        return LegacyDatabase.GetMapTiles(Index);
                    }
                }
            }
        }

        //Items & Resources
        private void SpawnAttributeItems()
        {
            ResourceSpawns.Clear();
            for (int x = 0; x < Options.MapWidth; x++)
            {
                for (int y = 0; y < Options.MapHeight; y++)
                {
                    if (Attributes[x, y] != null)
                    {
                        if (Attributes[x, y].Value == (int) MapAttributes.Item)
                        {
                            SpawnAttributeItem(x, y);
                        }
                        else if (Attributes[x, y].Value == (int) MapAttributes.Resource)
                        {
                            SpawnAttributeResource(x, y);
                        }
                    }
                }
            }
        }

        public void SpawnItem(int x, int y, Item item, int amount)
        {
            var itemBase = ItemBase.Lookup.Get<ItemBase>(item.ItemNum);
            if (itemBase != null)
            {
                MapItems.Add(new MapItem(item.ItemNum, item.ItemVal,item.BagId, item.Bag));
                MapItems[MapItems.Count - 1].X = x;
                MapItems[MapItems.Count - 1].Y = y;
                MapItems[MapItems.Count - 1].DespawnTime = Globals.System.GetTimeMs() + Options.ItemDespawnTime;
                if (itemBase.ItemType == (int) ItemTypes.Equipment)
                {
                    MapItems[MapItems.Count - 1].ItemVal = 1;
                    for (int i = 0; i < (int) Stats.StatCount; i++)
                    {
                        MapItems[MapItems.Count - 1].StatBoost[i] = item.StatBoost[i];
                    }
                }
                else
                {
                    MapItems[MapItems.Count - 1].ItemVal = amount;
                }
                PacketSender.SendMapItemUpdate(Index, MapItems.Count - 1);
            }
        }

        private void SpawnAttributeItem(int x, int y)
        {
            var item = ItemBase.Lookup.Get<ItemBase>(Attributes[x, y].Data1);
            if (item != null)
            {
                MapItems.Add(new MapItem(Attributes[x, y].Data1, Attributes[x, y].Data2));
                MapItems[MapItems.Count - 1].X = x;
                MapItems[MapItems.Count - 1].Y = y;
                MapItems[MapItems.Count - 1].DespawnTime = -1;
                MapItems[MapItems.Count - 1].AttributeSpawnX = x;
                MapItems[MapItems.Count - 1].AttributeSpawnY = y;
                if (item.ItemType == (int) ItemTypes.Equipment)
                {
                    MapItems[MapItems.Count - 1].ItemVal = 1;
                    Random r = new Random();
                    for (int i = 0; i < (int) Stats.StatCount; i++)
                    {
                        MapItems[MapItems.Count - 1].StatBoost[i] = r.Next(-1 * item.StatGrowth, item.StatGrowth + 1);
                    }
                }
                PacketSender.SendMapItemUpdate(Index, MapItems.Count - 1);
            }
        }

        public void RemoveItem(int index, bool respawn = true)
        {
            if (index < MapItems.Count && MapItems[index] != null)
            {
                MapItems[index].ItemNum = -1;
                PacketSender.SendMapItemUpdate(Index, index);
                if (respawn)
                {
                    if (MapItems[index].AttributeSpawnX > -1)
                    {
                        ItemRespawns.Add(new MapItemSpawn());
                        ItemRespawns[ItemRespawns.Count - 1].AttributeSpawnX = MapItems[index].AttributeSpawnX;
                        ItemRespawns[ItemRespawns.Count - 1].AttributeSpawnY = MapItems[index].AttributeSpawnY;
                        ItemRespawns[ItemRespawns.Count - 1].RespawnTime = Globals.System.GetTimeMs() +
                                                                           Options.ItemRepawnTime;
                    }
                }
                MapItems[index] = null;
            }
        }

        public void DespawnItems()
        {
            //Kill all items resting on map
            ItemRespawns.Clear();
            for (int i = 0; i < MapItems.Count; i++)
            {
                RemoveItem(i, false);
            }
            MapItems.Clear();
        }

        public void DespawnItemsOf(ItemBase itemBase)
        {
            for (int i = 0; i < MapItems.Count; i++)
            {
                if (MapItems[i] != null)
                {
                    if (ItemBase.Lookup.Get<ItemBase>(MapItems[i].ItemNum) == itemBase)
                    {
                        RemoveItem(i, true);
                    }
                }
            }
        }

        // Resources
        private void SpawnAttributeResource(int x, int y)
        {
            var tempResource = new ResourceSpawn()
            {
                ResourceNum = Attributes[x, y].Data1,
                X = x,
                Y = y,
                Z = Attributes[x, y].Data2
            };
            ResourceSpawns.Add(tempResource);
        }

        private void SpawnMapResources()
        {
            for (int i = 0; i < ResourceSpawns.Count; i++)
            {
                SpawnMapResource(i);
            }
        }

        private void SpawnMapResource(int i)
        {
            lock (GetMapLock())
            {
                int x = ResourceSpawns[i].X;
                int y = ResourceSpawns[i].Y;
                int index = -1;
                MapResourceSpawn resourceSpawnInstance;
                if (ResourceSpawnInstances.ContainsKey(ResourceSpawns[i]))
                {
                    resourceSpawnInstance = ResourceSpawnInstances[ResourceSpawns[i]];
                }
                else
                {
                    resourceSpawnInstance = new MapResourceSpawn();
                    ResourceSpawnInstances.Add(ResourceSpawns[i], resourceSpawnInstance);
                }
                if (resourceSpawnInstance.Entity == null)
                {
                    var resourceBase = ResourceBase.Lookup.Get<ResourceBase>(ResourceSpawns[i].ResourceNum);
                    if (resourceBase != null)
                    {
                        index = Globals.FindOpenEntity();
                        Globals.Entities[index] = new Resource(index, resourceBase);
                        resourceSpawnInstance.Entity = (Resource) Globals.Entities[index];
                        Globals.Entities[index].X = ResourceSpawns[i].X;
                        Globals.Entities[index].Y = ResourceSpawns[i].Y;
                        Globals.Entities[index].Z = ResourceSpawns[i].Z;
                        Globals.Entities[index].MapIndex = Index;
                        mEntities.Add((Resource) Globals.Entities[index]);
                    }
                }
                else
                {
                    index = resourceSpawnInstance.Entity.MyIndex;
                }
                if (index > -1)
                {
                    ((Resource) Globals.Entities[index]).Spawn();
                }
            }
        }

        private void DespawnResources()
        {
            //Kill all resources spawned from this map
            foreach (var resourceSpawn in ResourceSpawnInstances)
            {
                if (resourceSpawn.Value != null && resourceSpawn.Value.Entity != null)
                {
                    resourceSpawn.Value.Entity.Destroy(0);
                    mEntities.Remove(resourceSpawn.Value.Entity);
                }
            }
            ResourceSpawnInstances.Clear();
        }

        //Npcs
        private void SpawnMapNpcs()
        {
            for (int i = 0; i < Spawns.Count; i++)
            {
                SpawnMapNpc(i);
            }
        }

        private void SpawnMapNpc(int i)
        {
            int x = 0;
            int y = 0;
            int dir = 0;
            var npcBase = NpcBase.Lookup.Get<NpcBase>(Spawns[i].NpcNum);
            if (npcBase != null)
            {
                MapNpcSpawn npcSpawnInstance;
                if (NpcSpawnInstances.ContainsKey(Spawns[i]))
                {
                    npcSpawnInstance = NpcSpawnInstances[Spawns[i]];
                }
                else
                {
                    npcSpawnInstance = new MapNpcSpawn();
                    NpcSpawnInstances.Add(Spawns[i], npcSpawnInstance);
                }
                if (Spawns[i].Dir >= 0)
                {
                    dir = Spawns[i].Dir;
                }
                else
                {
                    dir = Globals.Rand.Next(0, 4);
                }
                if (Spawns[i].X >= 0 && Spawns[i].Y >= 0)
                {
                    npcSpawnInstance.Entity = SpawnNpc(Spawns[i].X, Spawns[i].Y, dir, Spawns[i].NpcNum);
                }
                else
                {
                    for (int n = 0; n < 100; n++)
                    {
                        x = Globals.Rand.Next(0, Options.MapWidth);
                        y = Globals.Rand.Next(0, Options.MapHeight);
                        if (Attributes[x, y] == null || Attributes[x, y].Value == (int) MapAttributes.Walkable)
                        {
                            break;
                        }
                        x = 0;
                        y = 0;
                    }
                    npcSpawnInstance.Entity = SpawnNpc(x, y, dir, Spawns[i].NpcNum);
                }
            }
        }

        private void DespawnNpcs()
        {
            //Kill all npcs spawned from this map
            foreach (var npcSpawn in NpcSpawnInstances)
            {
                npcSpawn.Value.Entity.Die(0);
            }
            NpcSpawnInstances.Clear();
            Spawns.Clear();
            //Kill any other npcs on this map (only players should remain)
            var entities = mEntities.ToArray();
            foreach (var entity in entities)
            {
                if (entity.GetType() == typeof(Npc))
                {
                    entity.Die(0);
                }
            }
        }

        public EntityInstance SpawnNpc(int tileX, int tileY, int dir, int npcNum, bool despawnable = false)
        {
            var npcBase = NpcBase.Lookup.Get<NpcBase>(npcNum);
            if (npcBase != null)
            {
                int entityIndex = Globals.FindOpenEntity();
                Globals.Entities[entityIndex] = new Npc(entityIndex, npcBase, despawnable)
                {
                    MapIndex = Index,
                    X = tileX,
                    Y = tileY,
                    Dir = dir
                };

                AddEntity((Npc) Globals.Entities[entityIndex]);
                PacketSender.SendEntityDataToProximity(Globals.Entities[entityIndex]);
                return (Npc) Globals.Entities[entityIndex];
            }
            return null;
        }

        //Events
        private void SpawnGlobalEvents()
        {
            GlobalEventInstances.Clear();
            foreach (var evt in Events)
            {
                if (evt.Value.IsGlobal == 1)
                {
                    GlobalEventInstances.Add(evt.Value, new EventInstance(evt.Value, evt.Key, Index));
                }
            }
        }

        private void DespawnGlobalEvents()
        {
            //Kill global events on map (make sure processing stops for online players)
            //Gonna rely on GC for now
            GlobalEventInstances.Clear();
        }

        public EventInstance GetGlobalEventInstance(EventBase baseEvent)
        {
            if (GlobalEventInstances.ContainsKey(baseEvent))
            {
                return GlobalEventInstances[baseEvent];
            }
            return null;
        }

        public bool FindEvent(EventBase baseEvent, EventPageInstance globalClone)
        {
            if (GlobalEventInstances.ContainsKey(baseEvent))
            {
                for (int i = 0; i < GlobalEventInstances[baseEvent].GlobalPageInstance.Length; i++)
                {
                    if (GlobalEventInstances[baseEvent].GlobalPageInstance[i] == globalClone)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        //Spawn a projectile
        public void SpawnMapProjectile(EntityInstance owner, ProjectileBase projectile, SpellBase parentSpell,
            ItemBase parentItem, int map, int x, int y, int z, int direction, EntityInstance target)
        {
            lock (GetMapLock())
            {
                int n = Globals.FindOpenEntity();
                MapProjectiles.Add(new Projectile(n, owner, parentSpell, parentItem, projectile, map, x, y, z,
                    direction,
                    target));
                Globals.Entities[n] = MapProjectiles[MapProjectiles.Count - 1];
                PacketSender.SendEntityDataToProximity(Globals.Entities[n]);
            }
        }

        public void DespawnProjectiles()
        {
            lock (GetMapLock())
            {
                //Clear Map Projectiles
                for (int i = 0; i < MapProjectiles.Count; i++)
                {
                    if (MapProjectiles[i] != null)
                    {
                        mEntities.Remove(MapProjectiles[i]);
                        MapProjectiles[i].Die();
                    }
                }
                MapProjectiles.Clear();
            }
        }

        //Entity Processing
        public void AddEntity(EntityInstance en)
        {
            if (en != null)
            {
                lock (GetMapLock())
                {
                    if (mEntities.IndexOf(en) == -1)
                    {
                        mEntities.Add(en);
                        if (en.GetType() == typeof(Player))
                        {
                            mPlayers.Add((Player) en);
                        }
                    }
                }
            }
        }

        public void RemoveEntity(EntityInstance en)
        {
            lock (GetMapLock())
            {
                mEntities.Remove(en);
                if (mPlayers.Contains(en))
                {
                    mPlayers.Remove((Player) en);
                }
            }
        }

        public void RemoveProjectile(Projectile en)
        {
            lock (GetMapLock())
            {
                MapProjectiles.Remove(en);
            }
        }

        public void ClearEntityTargetsOf(EntityInstance en)
        {
            lock (GetMapLock())
            {
                foreach (var entity in mEntities)
                {
                    if (entity.GetType() == typeof(Npc) && ((Npc) entity).MyTarget == en)
                    {
                        ((Npc) entity).RemoveTarget();
                    }
                }
            }
        }

        //Update + Related Functions
        public void Update(long timeMs)
        {
            lock (GetMapLock())
            {
                //See if we should dispose of tile data
                if (TileAccessTime + 30000 < timeMs && TileData != null)
                {
                    TileData = null;
                }
                if (!Active || CheckActive() == false || LastUpdateTime + UpdateDelay > timeMs)
                {
                    return;
                }
                //Process Items
                lock (MapItems)
                {
                    for (int i = 0; i < MapItems.Count; i++)
                    {
                        if (MapItems[i] != null && MapItems[i].DespawnTime != -1 &&
                            MapItems[i].DespawnTime < timeMs)
                        {
                            RemoveItem(i);
                        }
                    }
                    for (int i = 0; i < ItemRespawns.Count; i++)
                    {
                        if (ItemRespawns[i].RespawnTime < timeMs)
                        {
                            SpawnAttributeItem(ItemRespawns[i].AttributeSpawnX, ItemRespawns[i].AttributeSpawnY);
                            ItemRespawns.RemoveAt(i);
                        }
                    }
                    //Process All Entites
                    for (int i = 0; i < mEntities.Count; i++)
                    {
                        //Let's see if and how long this map has been inactive, if longer than 30 seconds, regenerate everything on the map
                        //TODO, take this 30 second value and throw it into the server config after I switch everything to json
                        if (timeMs > LastUpdateTime + 30000)
                        {
                            //Regen Everything & Forget Targets
                            if (mEntities[i].GetType() == typeof(Resource) || mEntities[i].GetType() == typeof(Npc))
                            {
                                mEntities[i].RestoreVital(Vitals.Health);
                                mEntities[i].RestoreVital(Vitals.Mana);
                                mEntities[i].Target = null;
                            }
                        }
                        mEntities[i].Update(timeMs);
                    }
                    //Process NPC Respawns
                    for (int i = 0; i < Spawns.Count; i++)
                    {
                        if (NpcSpawnInstances.ContainsKey(Spawns[i]))
                        {
                            MapNpcSpawn npcSpawnInstance = NpcSpawnInstances[Spawns[i]];
                            if (npcSpawnInstance != null && !Globals.Entities.Contains(npcSpawnInstance.Entity))
                            {
                                if (npcSpawnInstance.RespawnTime == -1)
                                {
                                    npcSpawnInstance.RespawnTime = Globals.System.GetTimeMs() +
                                                                   ((Npc) npcSpawnInstance.Entity).MyBase
                                                                   .SpawnDuration *
                                                                   1000 - (Globals.System.GetTimeMs() - LastUpdateTime);
                                }
                                else if (npcSpawnInstance.RespawnTime < Globals.System.GetTimeMs())
                                {
                                    SpawnMapNpc(i);
                                    npcSpawnInstance.RespawnTime = -1;
                                }
                            }
                        }
                    }
                    //Process Resource Respawns
                    for (int i = 0; i < ResourceSpawns.Count; i++)
                    {
                        if (ResourceSpawnInstances.ContainsKey(ResourceSpawns[i]))
                        {
                            MapResourceSpawn resourceSpawnInstance = ResourceSpawnInstances[ResourceSpawns[i]];
                            if (resourceSpawnInstance.Entity != null && resourceSpawnInstance.Entity.IsDead)
                            {
                                if (resourceSpawnInstance.RespawnTime == -1)
                                {
                                    resourceSpawnInstance.RespawnTime = Globals.System.GetTimeMs() +
                                                                        resourceSpawnInstance.Entity.MyBase
                                                                            .SpawnDuration * 1000;
                                }
                                else if (resourceSpawnInstance.RespawnTime < Globals.System.GetTimeMs())
                                {
                                    SpawnMapResource(i);
                                    resourceSpawnInstance.RespawnTime = -1;
                                }
                            }
                        }
                    }
                    //Process all of the projectiles
                    for (int i = 0; i < MapProjectiles.Count; i++)
                    {
                        MapProjectiles[i].Update();
                    }
                    //Process all global events
                    var evts = GlobalEventInstances.Values.ToList();
                    for (int i = 0; i < evts.Count; i++)
                    {
                        //Only do movement processing on the first page.
                        //This is because global events need to keep all of their pages at the same tile
                        //Think about a global event moving randomly that needed to turn into a warewolf and back (separate pages)
                        //If they were in different tiles the transition would make the event jump
                        //Something like described here: https://www.ascensiongamedev.com/community/bug_tracker/intersect/events-randomly-appearing-and-disappearing-r983/
                        for (int x = 0; x < evts[i].GlobalPageInstance.Length; x++)
                        {
                            //Gotta figure out if any players are interacting with this event.
                            var active = false;
                            foreach (var map in GetSurroundingMaps(true))
                            {
                                foreach (var player in map.GetPlayersOnMap())
                                {
                                    var eventInstance = player.FindEvent(evts[i].GlobalPageInstance[x]);
                                    if (eventInstance != null && eventInstance.CallStack.Count > 0) active = true;
                                }
                            }
                            evts[i].GlobalPageInstance[x].Update(active, timeMs);
                        }
                    }
                }
                LastUpdateTime = timeMs;
            }
        }

        public List<MapInstance> GetSurroundingMaps(bool includingSelf = false)
        {
            Debug.Assert(Lookup != null, "Lookup != null");
            var maps = SurroundingMaps?.Select(mapNum => Lookup.Get<MapInstance>(mapNum)).Where(map => map != null).ToList() ?? new List<MapInstance>();
            if (includingSelf) maps.Add(this);
            return maps;
        }

        private bool CheckActive()
        {
            if (GetPlayersOnMap()?.Count > 0)
            {
                return true;
            }

            var surroundingMaps = GetSurroundingMaps(true);
            if (surroundingMaps?.Count > 0)
            {
                foreach (var t in surroundingMaps)
                {
                    var map = t;
                    if (map == null) continue;

                    if (Monitor.TryEnter(map.GetMapLock(), new TimeSpan(0, 0, 0, 0, 1)))
                    {
                        try
                        {
                            if (map.GetPlayersOnMap()?.Count > 0)
                            {
                                return true;
                            }
                        }
                        finally
                        {
                            Monitor.Exit(map.GetMapLock());
                        }
                    }
                    else
                    {
                        return true;
                    }
                }
            }

            Active = false;
            return false;
        }

        public List<EntityInstance> GetEntities()
        {
            var entities = new List<EntityInstance>();
            entities.AddRange(mEntities.ToArray());
            return entities;
        }

        public List<Player> GetPlayersOnMap()
        {
            List<Player> players = new List<Player>();
            players.AddRange(mPlayers.ToArray());
            return players;
        }

        public void PlayerEnteredMap(Player player)
        {
            lock (GetMapLock())
            {
                Active = true;
                //Send Entity Info to Everyone and Everyone to the Entity
                SendMapEntitiesTo(player);
                player.MyClient.SentMaps.Clear();
                PacketSender.SendMapItems(player.MyClient, Index);
                AddEntity(player);
                player.LastMapEntered = Index;
                if (SurroundingMaps.Count <= 0) return;
                foreach (var t in SurroundingMaps)
                {
                    Lookup.Get<MapInstance>(t).Active = true;
                    Lookup.Get<MapInstance>(t).SendMapEntitiesTo(player);
                    PacketSender.SendMapItems(player.MyClient, t);
                }
                PacketSender.SendEntityDataToProximity(player, player.MyClient);
            }
        }

        public void SendMapEntitiesTo(Player player)
        {
            if (player != null)
            {
                PacketSender.SendMapEntitiesTo(player.MyClient, mEntities);
                if (player.MapIndex == Index) player.SendEvents();
            }
        }

        public void ClearConnections(int side = -1)
        {
            if (side == -1 || side == (int) Directions.Up) Up = -1;
            if (side == -1 || side == (int) Directions.Down) Down = -1;
            if (side == -1 || side == (int) Directions.Left) Left = -1;
            if (side == -1 || side == (int) Directions.Right) Right = -1;
            LegacyDatabase.SaveGameObject(this);
        }

        public bool TileBlocked(int x, int y)
        {
            //Check if tile is a blocked attribute
            if (Attributes[x, y] != null && Attributes[x, y].Value == (int) MapAttributes.Blocked)
            {
                return true;
            }
            //See if there are any entities in the way
            var entities = GetEntities();
            for (int i = 0; i < entities.Count; i++)
            {
                if (entities[i] != null && entities[i].GetType() != typeof(Projectile))
                {
                    //If Npc or Player then blocked.. if resource then check
                    if (entities[i].Passable == 0 && entities[i].X == x && entities[i].Y == y)
                    {
                        return true;
                    }
                }
            }
            //Check Global Events
            foreach (var globalEvent in GlobalEventInstances)
            {
                if (globalEvent.Value != null && globalEvent.Value.PageInstance != null)
                {
                    if (globalEvent.Value.PageInstance.Passable == 0 && globalEvent.Value.PageInstance.X == x &&
                        globalEvent.Value.PageInstance.Y == y)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        //Map Reseting Functions
        public void DespawnEverything()
        {
            DespawnNpcs();
            DespawnItems();
            DespawnResources();
            DespawnProjectiles();
            DespawnGlobalEvents();
        }

        public void RespawnEverything()
        {
            SpawnAttributeItems();
            SpawnGlobalEvents();
            SpawnMapNpcs();
            SpawnMapResources();
        }

        public override void Delete() => Lookup?.Delete(this);
    }
}