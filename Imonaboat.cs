using Carbon;
using Carbon.Components;
using Newtonsoft.Json;
using Oxide.Game.Rust.Cui;
using System.Collections.Generic;
using UnityEngine;
using Timer = Oxide.Plugins.Timer;

namespace Carbon.Plugins
{
    [Info("Im On a Boat!", "Goo_", "1.0.1")]
    [Description("Beautifully simple boat controls with a nautical theme.")]
    public class Imonaboat : CarbonPlugin
    {
        #region Fields

        private const string PermUse = "imonaboat.use";
        private const string PermAdmin = "imonaboat.admin";
        private const string BoatUI = "Imonaboat.UI";

        private const string ImageRepoBase = "https://raw.githubusercontent.com/purnellbp/imonaboat-rust-plugin/refs/heads/main/img/";

        private static string Img(string file) => ImageRepoBase + file;
        private static string StatusImageUrl(string status) => Img("status-" + status.ToLowerInvariant() + ".png");

        private static readonly string BgImageUrl = Img("bg.png");
        // i misspelled the word "engines" in the repo and im too lazy to fix it.
        private static readonly string LabelEnginesUrl = Img("corroded-engiens.jpg");
        private static readonly string LabelThrottleUrl = Img("corroded-throttle.jpg");
        private static readonly string LabelSailsUrl = Img("corroded-sails.jpg");
        private static readonly string LabelAnchorUrl = Img("corroded-anchor.jpg");

        private static readonly string[] StatusImageNames =
        {
            "on", "off", "forward", "reverse", "idle", "furled", "unfurled", "anchored", "housed"
        };

        private static readonly string[] PluginImageUrls = BuildImageRegistry();

        private static string[] BuildImageRegistry()
        {
            var urls = new List<string>
            {
                BgImageUrl,
                LabelEnginesUrl,
                LabelThrottleUrl,
                LabelSailsUrl,
                LabelAnchorUrl
            };

            foreach (var name in StatusImageNames)
                urls.Add(StatusImageUrl(name));

            return urls.ToArray();
        }

        private const string CmdEngine = "boat.engine";
        private const string CmdSails = "boat.sails";
        private const string CmdAnchor = "boat.anchor";
        private const string CmdToggleUI = "boat.ui";

        private const float AnchorRefreshDelay = 3f;

        private const string HelmShortName = "steeringwheel.deployed";

        private const string EditorRoot = "Imonaboat.Editor";
        private const string EditorPanel = "Imonaboat.EditPanel";
        private const float EditStep = 0.01f;
        private const float EditMinW = 0.10f;
        private const float EditMinH = 0.04f;

        private PluginConfig _config;
        private readonly Dictionary<ulong, float> _lastUse = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, Timer> _refreshTimers = new Dictionary<ulong, Timer>();
        private readonly HashSet<ulong> _uiOpen = new HashSet<ulong>();
        private readonly Dictionary<ulong, EditSession> _editors = new Dictionary<ulong, EditSession>();

        private enum BoatAction { Engine, Sails, Anchor }

        private class EditSession
        {
            public float XMin, YMin, XMax, YMax;
        }

        #endregion

        #region Configuration

        private class PluginConfig
        {
            [JsonProperty("Require permission (imonaboat.use)")]
            public bool RequirePermission { get; set; }

            [JsonProperty("Require boat authorization (must be authed on the helm)")]
            public bool RequireBoatAuth { get; set; }

            [JsonProperty("Auto-show UI when taking the helm")]
            public bool AutoShowUI { get; set; }

            [JsonProperty("Enable keyboard controls while mounted (E/R/Ctrl/W/S)")]
            public bool EnableKeyboardControls { get; set; }

            [JsonProperty("UI refresh interval (seconds)")]
            public float UIRefreshInterval { get; set; }

            [JsonProperty("Command cooldown (seconds)")]
            public float CommandCooldown { get; set; }

            [JsonProperty("UI anchor min")]
            public string UIAnchorMin { get; set; }

            [JsonProperty("UI anchor max")]
            public string UIAnchorMax { get; set; }

            public PluginConfig()
            {
                RequirePermission = false;
                RequireBoatAuth = true;
                AutoShowUI = true;
                EnableKeyboardControls = true;
                UIRefreshInterval = 3f;
                CommandCooldown = 0.5f;
                UIAnchorMin = "0.285 0";
                UIAnchorMax = "0.715 0.1528";
            }
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
            }
            catch
            {
                PrintWarning("Config was invalid, regenerating defaults.");
                _config = new PluginConfig();
            }

            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Lifecycle

        private void Init()
        {
            LoadConfig();
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
        }

        private void OnServerInitialized()
        {
            EnsureImagesCached();
        }

        private void ReloadImages()
        {
            using (var cui = new CUI(CuiHandler))
                cui.ClearImages(PluginImageUrls);

            Puts($"Cleared {PluginImageUrls.Length} cached UI image(s); re-downloading from the repo.");
            EnsureImagesCached();
        }

        private void EnsureImagesCached(int attempt = 0)
        {
            var missing = new List<string>();

            using (var cui = new CUI(CuiHandler))
            {
                foreach (var url in PluginImageUrls)
                {
                    if (!string.IsNullOrEmpty(url) && !cui.HasImage(url))
                        missing.Add(url);
                }

                if (missing.Count > 0)
                    cui.QueueImages(missing);
            }

            if (missing.Count == 0)
            {
                if (attempt > 0)
                {
                    Puts("All UI images are cached in Carbon's ImageDatabase.");
                    RedrawOpenPanels();
                }
                return;
            }

            if (attempt >= 5)
            {
                PrintWarning($"{missing.Count} UI image(s) still downloading; the panel will use a solid background until they finish.");
                return;
            }

            Puts($"Queued {missing.Count} UI image(s) for download into Carbon's ImageDatabase (check {attempt + 1}/5).");
            timer.Once(5f, () => EnsureImagesCached(attempt + 1));
        }

        private void RedrawOpenPanels()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player != null && _uiOpen.Contains(player.userID))
                    ShowUI(player);
            }
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                StopRefresh(player);
                DestroyUI(player);
                CuiHelper.DestroyUi(player, EditorRoot);
            }

            _uiOpen.Clear();
            _editors.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            StopRefresh(player);
            _uiOpen.Remove(player.userID);

            if (_editors.Remove(player.userID))
                CuiHelper.DestroyUi(player, EditorRoot);
        }

        #endregion

        #region Keyboard Controls (at the helm)

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (!_config.EnableKeyboardControls) return;
            if (player == null || input == null) return;
            if (player.GetMounted() == null) return;

            bool e = input.WasJustPressed(BUTTON.USE);
            bool r = input.WasJustPressed(BUTTON.RELOAD);
            bool anchor = input.WasJustPressed(BUTTON.DUCK);
            bool fwd = input.WasJustPressed(BUTTON.FORWARD);
            bool rev = input.WasJustPressed(BUTTON.BACKWARD);

            if (!(e || r || anchor || fwd || rev)) return;

            if (_config.RequirePermission && !permission.UserHasPermission(player.UserIDString, PermUse)) return;
            if (!TryGetBoat(player, out var boat)) return;

            if (e) boat.SetAllEnginesOn(!EnginesAreOn(boat));
            if (r) boat.SetAllSailsOpen(!SailsAreOpen(boat));
            if (anchor) ToggleAnchor(player, boat);
            if (fwd) SetEngineDirection(boat, false);
            if (rev) SetEngineDirection(boat, true);

            RefreshUI(player);

            if (anchor)
            {
                timer.Once(AnchorRefreshDelay, () =>
                {
                    if (player != null && player.IsConnected)
                        RefreshUI(player);
                });
            }
        }

        private void SetEngineDirection(PlayerBoat boat, bool reverse)
        {
            if (boat.Engines?.Cached == null) return;

            foreach (var engine in boat.Engines.Cached)
            {
                if (engine == null) continue;
                engine.SetFlag(BaseEntity.Flags.Reserved3, reverse);
            }

            boat.SetAllEnginesOn(true);
        }

        #endregion

        #region Hooks

        private void OnEntityMounted(BaseMountable mountable, BasePlayer player)
        {
            if (!_config.AutoShowUI || player == null || mountable == null) return;
            if (!IsHelmMount(mountable)) return;
            if (!(mountable.GetParentEntity() is PlayerBoat boat)) return;
            if (!IsAllowed(player, boat)) return;

            ShowUI(player);
            StartRefresh(player);
        }

        private void OnEntityDismounted(BaseMountable mountable, BasePlayer player)
        {
            if (player == null || mountable == null) return;
            if (!IsHelmMount(mountable)) return;
            if (!(mountable.GetParentEntity() is PlayerBoat)) return;

            StopRefresh(player);
            DestroyUI(player);
        }

        #endregion

        #region Commands

        [ConsoleCommand(CmdEngine)]
        private void CcEngine(ConsoleSystem.Arg arg) => DoToggle(arg?.Player(), BoatAction.Engine);

        [ConsoleCommand(CmdSails)]
        private void CcSails(ConsoleSystem.Arg arg) => DoToggle(arg?.Player(), BoatAction.Sails);

        [ConsoleCommand(CmdAnchor)]
        private void CcAnchor(ConsoleSystem.Arg arg) => DoToggle(arg?.Player(), BoatAction.Anchor);

        [ConsoleCommand(CmdToggleUI)]
        private void CcToggleUI(ConsoleSystem.Arg arg) => ToggleUICommand(arg?.Player());

        [ChatCommand("boatui")]
        private void ChatToggleUI(BasePlayer player, string command, string[] args) => ToggleUICommand(player);

        [ChatCommand("boatreloadimages")]
        private void ChatReloadImages(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                player.ChatMessage("You don't have permission to reload boat UI images (imonaboat.admin).");
                return;
            }

            player.ChatMessage("Reloading boat UI images from the repo...");
            ReloadImages();
        }

        [ChatCommand("boatedit")]
        private void ChatEdit(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                player.ChatMessage("You don't have permission to edit the boat UI layout (imonaboat.admin).");
                return;
            }

            ParseAnchor(_config.UIAnchorMin, out float x1, out float y1);
            ParseAnchor(_config.UIAnchorMax, out float x2, out float y2);

            _editors[player.userID] = new EditSession { XMin = x1, YMin = y1, XMax = x2, YMax = y2 };
            DrawEditor(player);
        }

        [ConsoleCommand("boat.edit")]
        private void CcEdit(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;

            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin))
                return;

            if (!_editors.TryGetValue(player.userID, out var s))
                return;

            string action = arg.GetString(0).ToLowerInvariant();
            string dir = arg.GetString(1);

            switch (action)
            {
                case "width":
                    ResizeSession(s, dir == "+" ? EditStep : -EditStep, 0f);
                    break;
                case "height":
                    ResizeSession(s, 0f, dir == "+" ? EditStep : -EditStep);
                    break;
                case "grow":
                    ResizeSession(s, EditStep, EditStep * (CurrentHeight(s) / Mathf.Max(0.0001f, CurrentWidth(s))));
                    break;
                case "shrink":
                    ResizeSession(s, -EditStep, -EditStep * (CurrentHeight(s) / Mathf.Max(0.0001f, CurrentWidth(s))));
                    break;
                case "reset":
                    ParseAnchor(new PluginConfig().UIAnchorMin, out float dx1, out float dy1);
                    ParseAnchor(new PluginConfig().UIAnchorMax, out float dx2, out float dy2);
                    s.XMin = dx1; s.YMin = dy1; s.XMax = dx2; s.YMax = dy2;
                    break;
                case "save":
                    _config.UIAnchorMin = FormatPair(s.XMin, s.YMin);
                    _config.UIAnchorMax = FormatPair(s.XMax, s.YMax);
                    SaveConfig();
                    CloseEditor(player);
                    player.ChatMessage($"Boat UI layout saved ({_config.UIAnchorMin} / {_config.UIAnchorMax}).");
                    RedrawOpenPanels();
                    return;
                case "close":
                    CloseEditor(player);
                    return;
                default:
                    return;
            }

            DrawEditor(player);
        }

        [ConsoleCommand("boat.reloadimages")]
        private void CcReloadImages(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();

            if (player != null && !player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                arg.ReplyWith("You don't have permission to reload boat UI images (imonaboat.admin).");
                return;
            }

            ReloadImages();
            arg?.ReplyWith("Reloading boat UI images from the repo...");
        }

        #endregion

        #region Toggle Logic

        private void DoToggle(BasePlayer player, BoatAction action)
        {
            if (player == null) return;
            if (!CanUse(player)) return;
            if (!TryGetBoat(player, out var boat))
            {
                player.ChatMessage("You must be on your modular boat to do that.");
                return;
            }

            switch (action)
            {
                case BoatAction.Engine:
                    boat.SetAllEnginesOn(!EnginesAreOn(boat));
                    break;

                case BoatAction.Sails:
                    boat.SetAllSailsOpen(!SailsAreOpen(boat));
                    break;

                case BoatAction.Anchor:
                    ToggleAnchor(player, boat);
                    break;
            }

            RefreshUI(player);

            if (action == BoatAction.Anchor)
            {
                timer.Once(AnchorRefreshDelay, () =>
                {
                    if (player != null && player.IsConnected)
                        RefreshUI(player);
                });
            }
        }

        private void ToggleAnchor(BasePlayer player, PlayerBoat boat)
        {
            if (boat.Anchors?.Cached == null) return;

            bool anyLowered = AnchorIsLowered(boat);
            foreach (var anchor in boat.Anchors.Cached)
            {
                if (anchor == null) continue;

                if (anyLowered)
                    anchor.RaiseAnchor(player);
                else
                    anchor.LowerAnchor(player, false);
            }
        }

        private static bool EnginesAreOn(PlayerBoat boat)
        {
            if (boat.Engines?.Cached == null) return false;
            foreach (var engine in boat.Engines.Cached)
                if (engine != null && engine.IsOn()) return true;
            return false;
        }

        private static bool EnginesInReverse(PlayerBoat boat)
        {
            if (boat.Engines?.Cached == null) return false;
            foreach (var engine in boat.Engines.Cached)
                if (engine != null && engine.InReverse) return true;
            return false;
        }

        private static bool SailsAreOpen(PlayerBoat boat)
        {
            if (boat.Sails?.Cached == null) return false;
            foreach (var sail in boat.Sails.Cached)
                if (sail != null && sail.Lowered) return true;
            return false;
        }

        private static bool AnchorIsLowered(PlayerBoat boat)
        {
            if (boat.Anchors?.Cached == null) return false;
            foreach (var anchor in boat.Anchors.Cached)
                if (anchor != null && anchor.Lowered) return true;
            return false;
        }

        #endregion

        #region UI

        private void ToggleUICommand(BasePlayer player)
        {
            if (player == null) return;

            if (_uiOpen.Contains(player.userID))
            {
                StopRefresh(player);
                DestroyUI(player);
                return;
            }

            if (!TryGetBoat(player, out _))
            {
                player.ChatMessage("You must be on your modular boat to open the boat controls.");
                return;
            }

            ShowUI(player);
            StartRefresh(player);
        }

        private void ShowUI(BasePlayer player)
        {
            if (player == null) return;
            if (!TryGetBoat(player, out var boat))
            {
                DestroyUI(player);
                return;
            }

            DestroyUI(player);

            bool engineOn = EnginesAreOn(boat);
            bool reverse = EnginesInReverse(boat);
            bool sailsOpen = SailsAreOpen(boat);
            bool anchorDown = AnchorIsLowered(boat);

            ParseAnchor(_config.UIAnchorMin, out float xMin, out float yMin);
            ParseAnchor(_config.UIAnchorMax, out float xMax, out float yMax);

            using (var cui = new CUI(CuiHandler))
            {
                cui.v2.CreateParent(CUI.ClientPanels.Overlay, new LuiPosition(xMin, yMin, xMax, yMax), BoatUI);
                BuildPanelContent(cui, BoatUI, engineOn, reverse, sailsOpen, anchorDown);
                cui.v2.SendUi(player);
            }

            _uiOpen.Add(player.userID);
        }

        private void BuildPanelContent(CUI cui, string parent, bool engineOn, bool reverse, bool sailsOpen, bool anchorDown)
        {
            if (ImageReady(cui, BgImageUrl))
                cui.v2.CreateRawImageFromDb(parent, new LuiPosition(0f, 0f, 1f, 1f), new LuiOffset(0f, 0f, 0f, 0f), BgImageUrl, "1 1 1 1", parent + "_bg");

            string content = parent + "_content";
            cui.v2.CreatePanel(parent, new LuiPosition(0.06f, 0.22f, 0.94f, 0.80f), new LuiOffset(0f, 0f, 0f, 0f), "0 0 0 0", content);

            const int n = 4;
            int i = 0;
            AddStatusCell(cui, content, i++, n, LabelEnginesUrl, engineOn ? "on" : "off");
            AddStatusCell(cui, content, i++, n, LabelThrottleUrl, engineOn ? (reverse ? "reverse" : "forward") : "idle");
            AddStatusCell(cui, content, i++, n, LabelSailsUrl, sailsOpen ? "unfurled" : "furled");
            AddStatusCell(cui, content, i++, n, LabelAnchorUrl, anchorDown ? "anchored" : "housed");
        }

        private void AddStatusCell(CUI cui, string parent, int index, int count, string labelImageUrl, string status)
        {
            float cellW = 1f / count;
            float xMin = index * cellW;
            float xMax = (index + 1) * cellW;

            string cell = $"{parent}_cell{index}";
            cui.v2.CreatePanel(parent, new LuiPosition(xMin, 0f, xMax, 1f), new LuiOffset(4f, 0f, -4f, 0f), "0 0 0 0.32", cell);

            if (ImageReady(cui, labelImageUrl))
                cui.v2.CreateRawImageFromDb(cell, new LuiPosition(0.06f, 0.56f, 0.94f, 0.96f), new LuiOffset(0f, 0f, 0f, 0f), labelImageUrl, "1 1 1 1", cell + "_lblimg");

            string statusUrl = StatusImageUrl(status);
            if (ImageReady(cui, statusUrl))
                cui.v2.CreateRawImageFromDb(cell, new LuiPosition(0.06f, 0.08f, 0.94f, 0.50f), new LuiOffset(0f, 0f, 0f, 0f), statusUrl, "1 1 1 1", cell + "_valimg");
        }

        private static bool ImageReady(CUI cui, string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string png = cui.GetImage(url);
            return !string.IsNullOrEmpty(png) && png != "0";
        }

        private static void ParseAnchor(string value, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (string.IsNullOrEmpty(value)) return;

            var parts = value.Split(' ');
            if (parts.Length < 2) return;

            float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x);
            float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y);
        }

        private void DestroyUI(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, BoatUI);
            _uiOpen.Remove(player.userID);
        }

        private void RefreshUI(BasePlayer player)
        {
            if (player == null || !_uiOpen.Contains(player.userID)) return;
            ShowUI(player);
        }

        #endregion

        #region Layout Editor

        private static float CurrentWidth(EditSession s) => s.XMax - s.XMin;
        private static float CurrentHeight(EditSession s) => s.YMax - s.YMin;

        private void OnCuiDraggableDrag(BasePlayer player, string name, Vector3 position)
        {
            if (player == null || name != EditorPanel) return;
            if (!_editors.TryGetValue(player.userID, out var s)) return;

            float w = CurrentWidth(s);
            float h = CurrentHeight(s);

            float cx = Mathf.Clamp(position.x, w / 2f, 1f - w / 2f);
            float cyBottom = Mathf.Clamp(1f - position.y, h / 2f, 1f - h / 2f);

            s.XMin = cx - w / 2f;
            s.XMax = cx + w / 2f;
            s.YMin = cyBottom - h / 2f;
            s.YMax = cyBottom + h / 2f;

            DrawEditor(player);
        }

        private void ResizeSession(EditSession s, float dw, float dh)
        {
            float cx = (s.XMin + s.XMax) / 2f;
            float cy = (s.YMin + s.YMax) / 2f;

            float w = Mathf.Clamp(CurrentWidth(s) + dw, EditMinW, 1f);
            float h = Mathf.Clamp(CurrentHeight(s) + dh, EditMinH, 1f);

            s.XMin = Mathf.Clamp(cx - w / 2f, 0f, 1f - w);
            s.XMax = s.XMin + w;
            s.YMin = Mathf.Clamp(cy - h / 2f, 0f, 1f - h);
            s.YMax = s.YMin + h;
        }

        private void CloseEditor(BasePlayer player)
        {
            if (player == null) return;
            _editors.Remove(player.userID);
            CuiHelper.DestroyUi(player, EditorRoot);
        }

        private void DrawEditor(BasePlayer player)
        {
            if (player == null || !_editors.TryGetValue(player.userID, out var s)) return;

            float w = CurrentWidth(s);
            float h = CurrentHeight(s);

            using (var cui = new CUI(CuiHandler))
            {
                cui.v2.CreateParent(CUI.ClientPanels.Overlay, new LuiPosition(0f, 0f, 1f, 1f), EditorRoot);

                var dim = cui.v2.CreatePanel(EditorRoot, new LuiPosition(0f, 0f, 1f, 1f), new LuiOffset(0f, 0f, 0f, 0f), "0 0 0 0.55", EditorRoot + "_dim");
                dim.AddCursor();

                cui.v2.CreateDraggable(EditorRoot, new LuiPosition(s.XMin, s.YMin, s.XMax, s.YMax), new LuiOffset(0f, 0f, 0f, 0f), "0 0 0 0", null, true, false, false, -1f, false, EditorPanel);
                BuildPanelContent(cui, EditorPanel, true, false, true, false);

                string bar = EditorRoot + "_bar";
                cui.v2.CreatePanel(EditorRoot, new LuiPosition(0.30f, 0.04f, 0.70f, 0.20f), new LuiOffset(0f, 0f, 0f, 0f), "0.08 0.08 0.10 0.97", bar);

                cui.v2.CreateText(bar, new LuiPosition(0.02f, 0.80f, 0.98f, 0.98f), new LuiOffset(0f, 0f, 0f, 0f), 13, "1 1 1 1", "Drag the panel to move it. Resize with the buttons, then Save.", TextAnchor.MiddleCenter, bar + "_info");
                cui.v2.CreateText(bar, new LuiPosition(0.02f, 0.62f, 0.98f, 0.79f), new LuiOffset(0f, 0f, 0f, 0f), 11, "0.7 0.8 1 1", $"pos {s.XMin:0.00},{s.YMin:0.00}    size {w:0.00} x {h:0.00}", TextAnchor.MiddleCenter, bar + "_read");

                AddEditorButton(cui, bar, 0.02f, 0.34f, 0.17f, 0.56f, "boat.edit shrink", "- Smaller", "0.30 0.30 0.36 1");
                AddEditorButton(cui, bar, 0.18f, 0.34f, 0.33f, 0.56f, "boat.edit grow", "+ Bigger", "0.30 0.30 0.36 1");
                AddEditorButton(cui, bar, 0.35f, 0.34f, 0.50f, 0.56f, "boat.edit width -", "Width -");
                AddEditorButton(cui, bar, 0.51f, 0.34f, 0.66f, 0.56f, "boat.edit width +", "Width +");
                AddEditorButton(cui, bar, 0.67f, 0.34f, 0.82f, 0.56f, "boat.edit height -", "Height -");
                AddEditorButton(cui, bar, 0.83f, 0.34f, 0.98f, 0.56f, "boat.edit height +", "Height +");

                AddEditorButton(cui, bar, 0.02f, 0.06f, 0.25f, 0.30f, "boat.edit reset", "Reset", "0.55 0.42 0.15 1");
                AddEditorButton(cui, bar, 0.40f, 0.06f, 0.63f, 0.30f, "boat.edit save", "Save", "0.2 0.5 0.25 1");
                AddEditorButton(cui, bar, 0.75f, 0.06f, 0.98f, 0.30f, "boat.edit close", "Close", "0.5 0.2 0.2 1");

                CuiHelper.DestroyUi(player, EditorRoot);
                cui.v2.SendUi(player);
            }
        }

        private void AddEditorButton(CUI cui, string parent, float xMin, float yMin, float xMax, float yMax, string command, string label, string color = "0.2 0.3 0.45 1")
        {
            string name = parent + "_btn_" + (command.GetHashCode() & 0x7fffffff);
            cui.v2.CreateButton(parent, new LuiPosition(xMin, yMin, xMax, yMax), new LuiOffset(0f, 0f, 0f, 0f), command, color, false, name);
            cui.v2.CreateText(name, new LuiPosition(0f, 0f, 1f, 1f), new LuiOffset(0f, 0f, 0f, 0f), 11, "1 1 1 1", label, TextAnchor.MiddleCenter, name + "_t");
        }

        private static string FormatPair(float x, float y)
        {
            return x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " " +
                   y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

        #endregion

        #region Refresh Loop

        private void StartRefresh(BasePlayer player)
        {
            StopRefresh(player);
            float interval = Mathf.Max(1f, _config.UIRefreshInterval);

            _refreshTimers[player.userID] = timer.Every(interval, () =>
            {
                if (player == null || !player.IsConnected || !TryGetBoat(player, out _))
                {
                    StopRefresh(player);
                    DestroyUI(player);
                    return;
                }

                RefreshUI(player);
            });
        }

        private void StopRefresh(BasePlayer player)
        {
            if (player == null) return;
            if (_refreshTimers.TryGetValue(player.userID, out var t))
                t?.Destroy();
            _refreshTimers.Remove(player.userID);
        }

        #endregion

        #region Helpers

        private bool IsAllowed(BasePlayer player, PlayerBoat boat)
        {
            if (_config.RequirePermission && !permission.UserHasPermission(player.UserIDString, PermUse))
                return false;

            if (_config.RequireBoatAuth && boat != null && !boat.IsPlayerAuthed(player, true))
                return false;

            return true;
        }

        private bool CanUse(BasePlayer player)
        {
            if (_config.RequirePermission && !permission.UserHasPermission(player.UserIDString, PermUse))
            {
                player.ChatMessage("You don't have permission to use boat controls.");
                return false;
            }

            float now = Time.realtimeSinceStartup;
            if (_lastUse.TryGetValue(player.userID, out float last) && now - last < _config.CommandCooldown)
                return false;

            _lastUse[player.userID] = now;
            return true;
        }

        private static bool IsHelmMount(BaseMountable mountable) =>
            mountable != null && mountable.ShortPrefabName == HelmShortName;

        private bool TryGetBoat(BasePlayer player, out PlayerBoat boat)
        {
            boat = null;
            if (player == null) return false;

            var mounted = player.GetMounted();
            boat = mounted != null
                ? mounted.GetParentEntity() as PlayerBoat
                : player.GetParentEntity() as PlayerBoat;

            if (boat == null) return false;
            if (_config.RequireBoatAuth && !boat.IsPlayerAuthed(player, true)) return false;

            return true;
        }

        #endregion
    }
}
