using System;
using System.Collections.Generic;
using System.Linq;
using Carbon.Core;
using Carbon.Plugins;
using UnityEngine;
using CUI;

namespace Carbon.Plugins
{
    [Info("Snake80s", "ChatGPT", "1.0.0")]
    [Description("Fully playable 8-bit Snake clone with UI, Start/Exit/Leaderboard.")]
    public class Snake80s : CarbonPlugin
    {
        private const string PermUse = "snake80s.use";
        private const string PermAdmin = "snake80s.admin";

        private const int BoardW = 18;
        private const int BoardH = 12;

        private const float TickSeconds = 0.20f;

        private const int UiW = 560;
        private const int UiH = 380;

        private const int Margin = 16;
        private const int HeaderH = 56;
        private const int FooterH = 64;

        private const string ColBg = "0.05 0.05 0.07 0.92";
        private const string ColPanel = "0.10 0.10 0.14 0.95";
        private const string ColText = "0.90 0.95 1.00 1.00";
        private const string ColGrid = "0.15 0.15 0.20 0.90";

        private readonly Dictionary<ulong, Session> _sessions = new();
        private readonly Dictionary<ulong, int> _bestScore = new();

        private Timer _tickTimer;

        private string _cmdUiClose;
        private string _cmdStart;
        private string _cmdExit;
        private string _cmdShowLb;

        private string _cmdUp;
        private string _cmdDown;
        private string _cmdLeft;
        private string _cmdRight;

        private System.Random _rng;

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
            _rng = new System.Random();
        }

        private void OnServerInitialized()
        {
            _cmdUiClose = Community.Protect("snake80s.close");
            _cmdStart = Community.Protect("snake80s.start");
            _cmdExit = Community.Protect("snake80s.exit");
            _cmdShowLb = Community.Protect("snake80s.leaderboard");

            _cmdUp = Community.Protect("snake80s.dir.up");
            _cmdDown = Community.Protect("snake80s.dir.down");
            _cmdLeft = Community.Protect("snake80s.dir.left");
            _cmdRight = Community.Protect("snake80s.dir.right");

            _tickTimer = timer.Every(TickSeconds, TickAllSessions);
        }

        private void Unload()
        {
            if (_tickTimer != null) _tickTimer.Destroy();
            foreach (var player in BasePlayer.activePlayerList)
                DestroyUi(player);

            _sessions.Clear();
        }

        [ChatCommand("snake")]
        private void CmdSnake(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsConnected) return;

            if (!permission.UserHasPermission(player.UserIDString, PermUse))
            {
                player.ChatMessage("You don't have permission to use Snake.");
                return;
            }

            EnsureSession(player);
            ShowMainUi(player, showLeaderboard: false);
        }

        [ProtectedCommand("snake80s.close")]
        private void UiClose(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player;
            if (player == null || !player.IsConnected) return;
            DestroyUi(player);
        }

        [ProtectedCommand("snake80s.start")]
        private void UiStart(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player;
            if (player == null || !player.IsConnected) return;

            var s = EnsureSession(player);
            ResetGame(s);
            ShowMainUi(player, showLeaderboard: false);
        }

        [ProtectedCommand("snake80s.exit")]
        private void UiExit(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player;
            if (player == null || !player.IsConnected) return;

            if (_sessions.TryGetValue(player.userID, out var s))
                s.Running = false;

            DestroyUi(player);
        }

        [ProtectedCommand("snake80s.leaderboard")]
        private void UiLeaderboard(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player;
            if (player == null || !player.IsConnected) return;

            EnsureSession(player);
            ShowMainUi(player, showLeaderboard: true);
        }

        [ProtectedCommand("snake80s.dir.up")]
        private void UiDirUp(ConsoleSystem.Arg arg) => SetDir(arg, Dir.Up);

        [ProtectedCommand("snake80s.dir.down")]
        private void UiDirDown(ConsoleSystem.Arg arg) => SetDir(arg, Dir.Down);

        [ProtectedCommand("snake80s.dir.left")]
        private void UiDirLeft(ConsoleSystem.Arg arg) => SetDir(arg, Dir.Left);

        [ProtectedCommand("snake80s.dir.right")]
        private void UiDirRight(ConsoleSystem.Arg arg) => SetDir(arg, Dir.Right);

        private void SetDir(ConsoleSystem.Arg arg, Dir dir)
        {
            var player = arg?.Player;
            if (player == null || !player.IsConnected) return;

            if (_sessions.TryGetValue(player.userID, out var s))
            {
                if (IsOpposite(s.PendingDir, dir)) return;
                s.PendingDir = dir;
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _sessions.Remove(player.userID);
        }

        private void TickAllSessions()
        {
            if (_sessions.Count == 0) return;

            var userIds = _sessions.Keys.ToArray();
            foreach (var userId in userIds)
            {
                if (!_sessions.TryGetValue(userId, out var s)) continue;
                if (!s.Running) continue;

                var player = BasePlayer.FindByID(userId);
                if (player == null || !player.IsConnected) continue;

                TickOne(s);
                UpdateGameUi(player, s);
            }
        }

        private void TickOne(Session s)
        {
            s.Dir = s.PendingDir;

            var head = s.Snake[0];
            var next = Step(head, s.Dir);

            next.x = Mod(next.x, BoardW);
            next.y = Mod(next.y, BoardH);

            if (s.SnakeSet.Contains(next))
            {
                s.Running = false;
                s.GameOver = true;
                TrySetBest(s.OwnerId, s.Score);
                return;
            }

            s.Snake.Insert(0, next);
            s.SnakeSet.Add(next);

            if (next == s.Food)
            {
                s.Score += 10;
                SpawnFood(s);
            }
            else
            {
                var tail = s.Snake[s.Snake.Count - 1];
                s.Snake.RemoveAt(s.Snake.Count - 1);
                s.SnakeSet.Remove(tail);
            }
        }

        private Session EnsureSession(BasePlayer player)
        {
            if (_sessions.TryGetValue(player.userID, out var existing))
                return existing;

            var s = new Session { OwnerId = player.userID };
            ResetGame(s);

            _sessions[player.userID] = s;
            return s;
        }

        private void ResetGame(Session s)
        {
            s.Score = 0;
            s.Running = false;
            s.GameOver = false;

            s.Dir = Dir.Right;
            s.PendingDir = Dir.Right;

            s.Snake = new List<Vec2i>();
            s.SnakeSet = new HashSet<Vec2i>();

            var start = new Vec2i(BoardW / 2, BoardH / 2);
            s.Snake.Add(start);
            s.SnakeSet.Add(start);

            for (int i = 1; i < 4; i++)
            {
                var p = new Vec2i(start.x - i, start.y);
                p.x = Mod(p.x, BoardW);
                s.Snake.Add(p);
                s.SnakeSet.Add(p);
            }

            SpawnFood(s);
            s.Running = true;
        }

        private void SpawnFood(Session s)
        {
            for (int i = 0; i < 200; i++)
            {
                var p = new Vec2i(_rng.Next(0, BoardW), _rng.Next(0, BoardH));
                if (!s.SnakeSet.Contains(p))
                {
                    s.Food = p;
                    return;
                }
            }

            for (int y = 0; y < BoardH; y++)
            for (int x = 0; x < BoardW; x++)
            {
                var p = new Vec2i(x, y);
                if (!s.SnakeSet.Contains(p))
                {
                    s.Food = p;
                    return;
                }
            }

            s.Running = false;
            s.GameOver = true;
            TrySetBest(s.OwnerId, s.Score);
        }

        private void TrySetBest(ulong userId, int score)
        {
            if (!_bestScore.TryGetValue(userId, out var best) || score > best)
                _bestScore[userId] = score;
        }

        private List<(string name, ulong id, int score)> GetTop10()
        {
            return _bestScore
                .Select(kvp =>
                {
                    var p = BasePlayer.FindByID(kvp.Key);
                    var name = p != null ? p.displayName : kvp.Key.ToString();
                    return (name, kvp.Key, kvp.Value);
                })
                .OrderByDescending(x => x.Value)
                .Take(10)
                .Select(x => (x.name, x.Key, x.Value))
                .ToList();
        }

        private const string UiRootName = "snake80s.ui.root";
        private const string UiBoardName = "snake80s.ui.board";
        private const string UiTextScore = "snake80s.ui.score";
        private const string UiTextState = "snake80s.ui.state";
        private const string UiLbPanel = "snake80s.ui.lb";

        private void ShowMainUi(BasePlayer player, bool showLeaderboard)
        {
            DestroyUi(player);

            using (var cui = new CUI.CUI(CuiHandler))
            {
                var parent = cui.v2.CreateParent(CUI.ClientPanels.Overlay, LuiPosition.Full, UiRootName);

                var frame = cui.v2.CreatePanel(
                    parent,
                    LuiPosition.MiddleCenter,
                    new LuiOffset(-(UiW / 2), -(UiH / 2), (UiW / 2), (UiH / 2)),
                    ColBg,
                    "snake80s.ui.frame"
                );

                var inner = cui.v2.CreatePanel(
                    frame,
                    LuiPosition.Full,
                    new LuiOffset(6, 6, UiW - 6, UiH - 6),
                    ColPanel,
                    "snake80s.ui.inner"
                );

                var header = cui.v2.CreatePanel(
                    inner,
                    LuiPosition.TopLeft,
                    new LuiOffset(Margin, UiH - HeaderH - Margin, UiW - Margin, UiH - Margin),
                    "0 0 0 0",
                    "snake80s.ui.header"
                );

                cui.v2.CreateText(
                    header,
                    LuiPosition.Full,
                    LuiOffset.Zero,
                    20,
                    ColText,
                    "S N A K E  8 0 s",
                    TextAnchor.MiddleLeft,
                    "snake80s.ui.title"
                );

                int btnW = 140;

                var btnStart = cui.v2.CreateButton(
                    inner,
                    LuiPosition.TopLeft,
                    new LuiOffset(UiW - Margin - (btnW * 3) - 20, UiH - Margin - 44, UiW - Margin - (btnW * 2) - 20, UiH - Margin - 10),
                    _cmdStart,
                    "0.15 0.65 0.25 1",
                    true,
                    "snake80s.ui.btn.start"
                );
                cui.v2.CreateText(btnStart, LuiPosition.Full, LuiOffset.Zero, 14, ColText, "START", TextAnchor.MiddleCenter);

                var btnLb = cui.v2.CreateButton(
                    inner,
                    LuiPosition.TopLeft,
                    new LuiOffset(UiW - Margin - (btnW * 2) - 10, UiH - Margin - 44, UiW - Margin - (btnW * 1) - 10, UiH - Margin - 10),
                    _cmdShowLb,
                    "0.20 0.35 0.80 1",
                    true,
                    "snake80s.ui.btn.lb"
                );
                cui.v2.CreateText(btnLb, LuiPosition.Full, LuiOffset.Zero, 14, ColText, "LEADERBOARD", TextAnchor.MiddleCenter);

                var btnExit = cui.v2.CreateButton(
                    inner,
                    LuiPosition.TopLeft,
                    new LuiOffset(UiW - Margin - btnW, UiH - Margin - 44, UiW - Margin, UiH - Margin - 10),
                    _cmdExit,
                    "0.80 0.20 0.20 1",
                    true,
                    "snake80s.ui.btn.exit"
                );
                cui.v2.CreateText(btnExit, LuiPosition.Full, LuiOffset.Zero, 14, ColText, "EXIT", TextAnchor.MiddleCenter);

                cui.v2.CreateText(
                    inner,
                    LuiPosition.TopLeft,
                    new LuiOffset(Margin, UiH - HeaderH - Margin - 28, UiW - Margin, UiH - HeaderH - Margin - 6),
                    14,
                    ColText,
                    "SCORE: 0    BEST: 0",
                    TextAnchor.MiddleLeft,
                    UiTextScore
                );

                cui.v2.CreateText(
                    inner,
                    LuiPosition.TopLeft,
                    new LuiOffset(Margin, UiH - HeaderH - Margin - 52, UiW - Margin, UiH - HeaderH - Margin - 30),
                    12,
                    "0.75 0.80 0.90 1",
                    "WASD / ARROWS to steer. Eat food. Don't bite yourself.",
                    TextAnchor.MiddleLeft,
                    UiTextState
                );

                int boardX0 = Margin;
                int boardY0 = FooterH + Margin;
                int boardX1 = UiW - Margin;
                int boardY1 = UiH - HeaderH - Margin - 60;

                cui.v2.CreatePanel(
                    inner,
                    LuiPosition.TopLeft,
                    new LuiOffset(boardX0, boardY0, boardX1, boardY1),
                    "0.03 0.03 0.04 0.95",
                    UiBoardName
                );

                BuildControls(cui, inner);

                if (showLeaderboard)
                    BuildLeaderboard(cui, inner);

                cui.v2.SendUi(player);
            }

            if (_sessions.TryGetValue(player.userID, out var s))
                UpdateGameUi(player, s);
        }

        private void BuildControls(CUI.CUI cui, string inner)
        {
            var footer = cui.v2.CreatePanel(
                inner,
                LuiPosition.BottomLeft,
                new LuiOffset(Margin, Margin, UiW - Margin, FooterH),
                "0 0 0 0",
                "snake80s.ui.footer"
            );

            int padSize = 38;
            int gap = 6;

            int padX = UiW - Margin - (padSize * 3 + gap * 2);
            int padY = 10;

            var bUp = cui.v2.CreateButton(
                footer, LuiPosition.BottomLeft,
                new LuiOffset(padX + padSize + gap, padY + padSize + gap, padX + padSize + gap + padSize, padY + padSize + gap + padSize),
                _cmdUp, ColGrid, true, "snake80s.ui.pad.up"
            );
            cui.v2.CreateText(bUp, LuiPosition.Full, LuiOffset.Zero, 16, ColText, "↑", TextAnchor.MiddleCenter);

            var bLeft = cui.v2.CreateButton(
                footer, LuiPosition.BottomLeft,
                new LuiOffset(padX, padY, padX + padSize, padY + padSize),
                _cmdLeft, ColGrid, true, "snake80s.ui.pad.left"
            );
            cui.v2.CreateText(bLeft, LuiPosition.Full, LuiOffset.Zero, 16, ColText, "←", TextAnchor.MiddleCenter);

            var bDown = cui.v2.CreateButton(
                footer, LuiPosition.BottomLeft,
                new LuiOffset(padX + padSize + gap, padY, padX + padSize + gap + padSize, padY + padSize),
                _cmdDown, ColGrid, true, "snake80s.ui.pad.down"
            );
            cui.v2.CreateText(bDown, LuiPosition.Full, LuiOffset.Zero, 16, ColText, "↓", TextAnchor.MiddleCenter);

            var bRight = cui.v2.CreateButton(
                footer, LuiPosition.BottomLeft,
                new LuiOffset(padX + (padSize + gap) * 2, padY, padX + (padSize + gap) * 2 + padSize, padY + padSize),
                _cmdRight, ColGrid, true, "snake80s.ui.pad.right"
            );
            cui.v2.CreateText(bRight, LuiPosition.Full, LuiOffset.Zero, 16, ColText, "→", TextAnchor.MiddleCenter);

            var bClose = cui.v2.CreateButton(
                footer,
                LuiPosition.BottomLeft,
                new LuiOffset(0, 10, 120, 44),
                _cmdUiClose,
                "0.35 0.35 0.40 1",
                true,
                "snake80s.ui.btn.close"
            );
            cui.v2.CreateText(bClose, LuiPosition.Full, LuiOffset.Zero, 14, ColText, "CLOSE", TextAnchor.MiddleCenter);
        }

        private void BuildLeaderboard(CUI.CUI cui, string inner)
        {
            var lb = cui.v2.CreatePanel(
                inner,
                LuiPosition.TopLeft,
                new LuiOffset(UiW - 240 - Margin, FooterH + Margin, UiW - Margin, UiH - HeaderH - Margin - 60),
                "0.06 0.06 0.09 0.95",
                UiLbPanel
            );

            cui.v2.CreateText(lb, LuiPosition.TopLeft, new LuiOffset(10, 0, 220, 30), 14, ColText, "TOP 10", TextAnchor.MiddleLeft);

            var top = GetTop10();
            int y = 30;
            for (int i = 0; i < top.Count; i++)
            {
                var entry = $"{i + 1,2}. {TrimName(top[i].name, 12),-12}  {top[i].score,4}";
                cui.v2.CreateText(lb, LuiPosition.TopLeft, new LuiOffset(10, y, 220, y + 22), 12, "0.85 0.90 1 1", entry, TextAnchor.MiddleLeft);
                y += 22;
            }

            if (top.Count == 0)
                cui.v2.CreateText(lb, LuiPosition.TopLeft, new LuiOffset(10, 34, 220, 58), 12, "0.75 0.80 0.90 1", "No scores yet.", TextAnchor.MiddleLeft);
        }

        private void UpdateGameUi(BasePlayer player, Session s)
        {
            var best = _bestScore.TryGetValue(player.userID, out var b) ? b : 0;

            var scoreLine = $"SCORE: {s.Score}    BEST: {best}";
            var stateLine = s.GameOver
                ? "GAME OVER. Press START to play again."
                : "WASD / ARROWS to steer. Eat food. Don't bite yourself.";

            char[,] grid = new char[BoardH, BoardW];
            for (int y = 0; y < BoardH; y++)
            for (int x = 0; x < BoardW; x++)
                grid[y, x] = '·';

            grid[s.Food.y, s.Food.x] = '●';

            for (int i = 0; i < s.Snake.Count; i++)
            {
                var p = s.Snake[i];
                grid[p.y, p.x] = (i == 0) ? '▓' : '█';
            }

            var lines = new List<string>(BoardH);
            for (int y = BoardH - 1; y >= 0; y--)
            {
                var row = new char[BoardW * 2];
                int idx = 0;
                for (int x = 0; x < BoardW; x++)
                {
                    row[idx++] = grid[y, x];
                    row[idx++] = ' ';
                }
                lines.Add(new string(row));
            }

            var boardText = string.Join("\n", lines);

            using (var cui = new CUI.CUI(CuiHandler))
            {
                cui.v2.UpdateText(UiTextScore, scoreLine, 14, ColText);
                cui.v2.UpdateText(UiTextState, stateLine, 12, "0.75 0.80 0.90 1");

                var boardTextName = "snake80s.ui.board.text";
                cui.v2.SetDestroy(boardTextName);

                int boardX0 = Margin;
                int boardY0 = FooterH + Margin;
                int boardX1 = UiW - Margin;
                int boardY1 = UiH - HeaderH - Margin - 60;

                cui.v2.CreateText(
                    UiBoardName,
                    LuiPosition.TopLeft,
                    new LuiOffset(10, 10, (boardX1 - boardX0) - 10, (boardY1 - boardY0) - 10),
                    14,
                    ColText,
                    boardText,
                    TextAnchor.UpperLeft,
                    boardTextName
                );

                var legendName = "snake80s.ui.legend";
                cui.v2.SetDestroy(legendName);
                cui.v2.CreateText(
                    UiBoardName,
                    LuiPosition.BottomLeft,
                    new LuiOffset(10, 2, 360, 22),
                    12,
                    "0.85 0.90 1 1",
                    "▓ head   █ body   ● food",
                    TextAnchor.LowerLeft,
                    legendName
                );

                cui.v2.SendUi(player);
            }
        }

        private void DestroyUi(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            using (var cui = new CUI.CUI(CuiHandler))
            {
                cui.v2.SetDestroy(UiRootName);
                cui.v2.SendUi(player);
            }
        }

        private static int Mod(int x, int m)
        {
            int r = x % m;
            return r < 0 ? r + m : r;
        }

        private static Vec2i Step(Vec2i p, Dir d)
        {
            return d switch
            {
                Dir.Up => new Vec2i(p.x, p.y + 1),
                Dir.Down => new Vec2i(p.x, p.y - 1),
                Dir.Left => new Vec2i(p.x - 1, p.y),
                _ => new Vec2i(p.x + 1, p.y),
            };
        }

        private static bool IsOpposite(Dir a, Dir b)
        {
            return (a == Dir.Up && b == Dir.Down) ||
                   (a == Dir.Down && b == Dir.Up) ||
                   (a == Dir.Left && b == Dir.Right) ||
                   (a == Dir.Right && b == Dir.Left);
        }

        private static string TrimName(string name, int max)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            return name.Length <= max ? name : name.Substring(0, max);
        }

        private enum Dir { Up, Down, Left, Right }

        private struct Vec2i : IEquatable<Vec2i>
        {
            public int x;
            public int y;

            public Vec2i(int x, int y) { this.x = x; this.y = y; }

            public bool Equals(Vec2i other) => x == other.x && y == other.y;
            public override bool Equals(object obj) => obj is Vec2i v && Equals(v);
            public override int GetHashCode() => (x * 397) ^ y;

            public static bool operator ==(Vec2i a, Vec2i b) => a.Equals(b);
            public static bool operator !=(Vec2i a, Vec2i b) => !a.Equals(b);
        }

        private class Session
        {
            public ulong OwnerId;

            public bool Running;
            public bool GameOver;

            public int Score;

            public Dir Dir;
            public Dir PendingDir;

            public List<Vec2i> Snake;
            public HashSet<Vec2i> SnakeSet;

            public Vec2i Food;
        }
    }
}
