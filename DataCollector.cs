using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Plugins;
using VRage.Utils;
using VRageMath;
using VRage.Input;

#nullable disable
namespace DataCollectorTest
{

    public class Player
    {
        public Vector3 position { get; private set; }
        private IMyPlayer local_player;
        public long identityId { get; private set; }
        public bool IsInitialized = false;

        public Player()
        {
            this.position = Vector3.Zero;
            this.local_player = null;
            this.identityId = 0L;
            this.IsInitialized = true;
        }

        public void UpdatePlayerPosition()
        {
            this.position = MySector.MainCamera.Position;
        }

        public void SetLocalPlayer()
        {
            if (this.local_player != null)
                return;
            
            this.local_player = MyAPIGateway.Session.LocalHumanPlayer;
            this.identityId = this.local_player.IdentityId;
        }
    }

    public class GridData
    {
        public string uuid { get; set; }
        public long entity_id { get; set; }
        public string grid_name { get; set; }
        public long owner_id { get; set; }
        public string faction_tag { get; set; }
    }

    public class PositionData
    {
        public string grid_uuid { get; set; }
        public float x { get; set; }
        public float y { get; set; }
        public float z { get; set; }
    }
    public class MyGridInRange
    {
        private MyCubeGrid grid;
        public long entityId { get; private set; }
        private string name;
        private long ownerId;
        private string factionTag;
        private Vector3 position { get; set; }
        private Vector3 last_position {  get; set; }
        public bool isPosted {  get; set; }

        public MyGridInRange (MyCubeGrid grid)
        {
            this.grid = grid;
            this.entityId = grid.EntityId;
            this.name = grid.DisplayName;
            this.ownerId = grid.BigOwners?.FirstOrDefault() ?? 0L;
            this.position = grid.PositionComp.GetPosition();
            this.last_position = this.position;


            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(ownerId);
            factionTag = faction.Tag ?? "Unknown";
        }

        public bool HasMoved()
        {
            this.updatePosition();
            return !position.Equals(last_position);
        }

        public string GridToJson()
        {
            var gridData = new GridData
            {
                uuid = $"{this.ownerId}_{Escape(this.name)}",
                entity_id = this.entityId,
                grid_name = this.name,
                owner_id = this.ownerId,
                faction_tag = this.factionTag
            };

            return JsonConvert.SerializeObject(gridData);
        }

        public string PositionToJson()
        {
            var positionData = new PositionData()
            {
                grid_uuid = $"{this.ownerId}_{Escape(this.name)}",
                x = this.position.X,
                y = this.position.Y,
                z = this.position.Z
            };

            return JsonConvert.SerializeObject(positionData);
        }

        private string Escape(string input)
        {
            return input?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
        }

        public void updatePosition()
        {
            this.last_position = this.position;
            this.position = this.grid.PositionComp.GetPosition();

        }
    }

    public class MyGridDetector
    {
        static readonly List<MyEntity> _entityCache = new List<MyEntity>();
        
        public Dictionary<long, MyGridInRange> gridsInRange = new Dictionary<long, MyGridInRange>();
        public Dictionary<long, MyGridInRange> newGridsInRange = new Dictionary<long, MyGridInRange>();

        public void DetectNewGrids(Player player, float syncRange)
        {
            _entityCache.Clear();

            player.UpdatePlayerPosition();
            BoundingSphereD sphere = new BoundingSphereD(player.position, syncRange);
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, _entityCache);

            foreach (var entity in _entityCache)
            {
                if (entity?.MarkedForClose != false) continue;
                
                if (entity is MyCubeGrid grid && grid.Physics != null && !grid.IsPreview)
                {
                    var group = MyCubeGridGroups.Static.Physical.GetGroup(grid);
                    if (group != null && group.Nodes.FirstOrDefault()?.NodeData != grid)
                        continue;

                    MyGridInRange detectedGrid = new MyGridInRange(grid);
                    newGridsInRange.Add(detectedGrid.entityId, detectedGrid);
                }
            }
        }

        public void CleanupDetectedGrids()
        {
            HashSet<long> toRemove = new HashSet<long>();

            foreach (var kvp in newGridsInRange)
            {
                if (!gridsInRange.ContainsKey(kvp.Key))
                {
                    gridsInRange.Add(kvp.Key, kvp.Value);
                }      
            }

            if (gridsInRange.Count > 0)
            {
                foreach (var kvp in gridsInRange)
                {
                    if (!newGridsInRange.ContainsKey(kvp.Key))
                    {
                        toRemove.Add(kvp.Key);
                    }
                }
            }

            foreach (var key in toRemove)
            {
                gridsInRange.Remove(key);
            }

            newGridsInRange.Clear();
        }
    }

    public class MyHTTPSender
    { 
        private static readonly HttpClient _client = new HttpClient();

        public async Task SendPostAsync (string url, string jsonBody)
        {
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            try
            {
                HttpResponseMessage response = await _client.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    MyLog.Default.WriteLine($"POST failed: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"POST failed: {ex.Message}");
            }
        }
    }

    public class MyPOSTDataHandler
    {
        private MyHTTPSender _sender = new MyHTTPSender();

        public void SendGridData(Dictionary<long, MyGridInRange> gridsInRange)
        {
            var gridsToSend = new List<string>();

            foreach (var kvp in gridsInRange)
            {
                if (!kvp.Value.isPosted)
                {
                    gridsToSend.Add(kvp.Value.GridToJson());
                    kvp.Value.isPosted = true;
                }
            }

            if (gridsToSend.Count == 0)
                return;

            string gridPayload = $"[{string.Join(",", gridsToSend)}]";

            _ = _sender.SendPostAsync("http://127.0.0.1:8000/api/v1/grids/", gridPayload);

        }

        public void SendPositionData(Dictionary<long, MyGridInRange> gridsInRange)
        {
            var positionsToSend = new List<string>();

            foreach (var kvp in gridsInRange)
            {
                if (!kvp.Value.HasMoved())
                    continue;
                positionsToSend.Add(kvp.Value.PositionToJson());
            }

            if (positionsToSend.Count == 0)
                return;

            string positionPayload = $"[{string.Join(",", positionsToSend)}]";

            _ = _sender.SendPostAsync("http://127.0.0.1:8000/api/v1/grid_positions/", positionPayload);
        }
    }

    public class CollectData : IPlugin, IDisposable
    {
        public Player player = new Player();
        private float syncRange;
        public MyGridDetector gridDetector = new MyGridDetector();
        public MyPOSTDataHandler postDataHandler = new MyPOSTDataHandler();

        private bool streamEnabled = false;

        private TimeSpan _updateInterval = TimeSpan.FromSeconds(2);
        private DateTime _lastUpdateTime = DateTime.MinValue;

        public void Init(object gameInstace) { }

        public void Update()
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Player == null || MySession.Static.LocalCharacter == null)
            {
                gridDetector.newGridsInRange.Clear();
                gridDetector.gridsInRange.Clear();
                return;
            }
                
            if (MySector.MainCamera == null)
                return;

            player.SetLocalPlayer();

            if (MyAPIGateway.Input.IsNewKeyPressed(MyKeys.RightControl))
            {
                streamEnabled = !streamEnabled;
                MyAPIGateway.Utilities.ShowMessage($"{nameof(DataCollectorTest)}", $"Data Streaming {(streamEnabled ? "enabled" : "disabled")}");
            }

            if (!streamEnabled)
            {
                return;
            }
                

            var now = DateTime.UtcNow;

            if (now - _lastUpdateTime >= _updateInterval)
            {
                _lastUpdateTime = now;

                syncRange = MyAPIGateway.Session.SessionSettings.SyncDistance;

                gridDetector.DetectNewGrids(player, syncRange);
                gridDetector.CleanupDetectedGrids();

                postDataHandler.SendGridData(gridDetector.gridsInRange);
                postDataHandler.SendPositionData(gridDetector.gridsInRange);
            }
        }

        public void Dispose() { }
    }
}
