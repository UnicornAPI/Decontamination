using System;
using System.Collections.Generic;
using System.Linq;
using Unicorn.API;
using Unicorn.Commands;
using Unicorn.Events;
using UnityEngine;
using Door = Unicorn.API.Door;

namespace Decontamination
{
    public sealed class Config
    {
        public bool Enabled = true;
        /// <summary>На какой минуте раунда начинается деконтаминация ЛЗС.</summary>
        public float StartMinute = 12f;
        /// <summary>Предупреждения: за сколько секунд до начала.</summary>
        public List<int> WarningSeconds = new() { 300, 60, 30, 10 };
        /// <summary>Минимум игроков на сервере, чтобы деконтаминация шла (0 — всегда).</summary>
        public int MinPlayers = 0;

        /// <summary>Двери КПП (уровень доступа Checkpoint) рядом с ЛЗС — закрыть и заблокировать.</summary>
        public bool LockCheckpoints = true;
        /// <summary>Остальные двери ЛЗС — открыть и заблокировать (нельзя запереться).</summary>
        public bool OpenAndLockLczDoors = true;
        /// <summary>Лифты, у которых хотя бы одна сторона в ЛЗС, — заблокировать.</summary>
        public bool LockElevators = true;
        /// <summary>Выключить свет в ЛЗС.</summary>
        public bool LightsOff = true;
        /// <summary>Шлюзы (Airlock) не трогать — у них своя логика пары дверей.</summary>
        public bool SkipAirlocks = true;
        /// <summary>Запасной вариант, если корень сцены у двери КПП не распознан: радиус поиска ламп ЛЗС, м.</summary>
        public float CheckpointRadius = 12f;

        /// <summary>Урон газа в секунду: максимум из «плоского» и «% от макс. HP», растёт со временем.</summary>
        public float DamagePerSecond = 10f;
        public float PercentMaxHpPerSecond = 2f;
        /// <summary>На сколько (в долях) растёт урон каждую секунду: 0.05 = +5% в секунду.</summary>
        public float DamageRampPerSecond = 0.05f;

        public string Prefix = "<color=#FFC940>C.A.S.S.I.E.</color> <color=#9A9A9A>|</color> ";
        public string WarningText = "Деконтаминация лёгкой зоны через {time}. Всему персоналу покинуть зону.";
        public string StartText = "Деконтаминация лёгкой зоны начата. КПП заблокированы.";
        public string PersonalWarning = "<color=#FF6A1A>Вы в лёгкой зоне!</color>\nИдите к КПП — через {time} выхода не будет";
        public string PersonalInside = "<color=#FF3B3B>Деконтаминация!</color>\nВыхода из лёгкой зоны нет";
        public string ElevatorBlocked = "<color=#FF3B3B>Лифт заблокирован:</color> деконтаминация лёгкой зоны";
        public float BroadcastSeconds = 8f;
    }

    /// <summary>
    /// Деконтаминация лёгкой зоны как в SCP:SL (жёсткая): отсчёт → КПП закрыты и заблокированы, двери ЛЗС открыты и
    /// заблокированы, лифты из ЛЗС стоят, свет гаснет, газ убивает всех в ЛЗС. В конце раунда всё возвращается как было.
    /// </summary>
    public sealed class DecontaminationPlugin : Plugin
    {
        public override string Name => "Decontamination";
        public override string Author => "FLX";

        private Config _cfg;
        private TimerHandle _timer;

        private enum Phase { Idle, Counting, Active, Cancelled }
        private Phase _phase = Phase.Idle;
        private float? _forcedStart;                // время раунда (с), когда начать — из команды
        private float _notBefore;                   // не раньше этого времени раунда (ждали MinPlayers — дать предупреждения)
        private float _lastErrLog = -999f;
        private readonly HashSet<int> _warned = new();
        private float _activeSince;                 // Time.time начала

        // что изменили — чтобы вернуть
        private readonly List<(Door Door, bool WasOpen, bool WasLocked)> _touchedDoors = new();
        private readonly HashSet<Player> _lightsOffFor = new();
        private readonly Dictionary<Player, float> _elevMsgAt = new();
        private Func<MapElevator, Player, bool> _elevatorBlocker;

        public override void OnEnabled()
        {
            _cfg = LoadConfig<Config>();
            if (!_cfg.Enabled) { Log.Info("[Decont] выключен"); return; }
            ServerEvents.RoundStarted += OnRoundStarted;
            ServerEvents.RoundEnded += OnRoundEnded;
            ServerEvents.WaitingForPlayers += OnRoundEnded;
            PlayerEvents.Joined += OnJoined;
            _elevatorBlocker = BlockElevator;
            Map.ElevatorBlockers.Add(_elevatorBlocker);
            _timer = Timing.CallRepeating(1f, Tick);
            RegisterCommand();
            Server.PublicInfo["Decontamination"] = new Dictionary<string, object>
            {
                ["start_minute"] = _cfg.StartMinute, ["checkpoints_locked"] = _cfg.LockCheckpoints, ["elevators_locked"] = _cfg.LockElevators,
                ["description"] = "На {0}-й минуте лёгкая зона закрывается: КПП блокируются, свет гаснет, газ убивает всех, кто остался.".Replace("{0}", _cfg.StartMinute.ToString("0.#")),
            };
            if (Round.IsStarted && Round.SessionData.ContainsKey(CancelKey)) _phase = Phase.Cancelled;   // отменили командой до reload
            else if (Round.IsStarted)   // включили посреди раунда (reload / рестарт плагина)
            {
                _phase = Phase.Counting;
                // время уже прошло — не начинать мгновенно, а дать 30 с с предупреждениями
                if (Round.ElapsedTime >= StartAt - 30f)
                {
                    _forcedStart = Round.ElapsedTime + 30f;
                    foreach (var w in _cfg.WarningSeconds) if (w > 30) _warned.Add(w);
                }
            }
            Log.Info($"[Decont] включён: старт на {_cfg.StartMinute:0.#} мин раунда");
        }

        public override void OnDisabled()
        {
            ServerEvents.RoundStarted -= OnRoundStarted;
            ServerEvents.RoundEnded -= OnRoundEnded;
            ServerEvents.WaitingForPlayers -= OnRoundEnded;
            PlayerEvents.Joined -= OnJoined;
            if (_elevatorBlocker != null) Map.ElevatorBlockers.Remove(_elevatorBlocker);
            _timer?.Cancel();
            Restore(true);
            Server.PublicInfo.TryRemove("Decontamination", out _);
        }

        private const string CancelKey = "decont.cancelled";

        private void OnRoundEnded() => Restore(true);

        private void OnRoundStarted()
        {
            // игра в начале раунда сама включает весь свет (RpcResetOldEvent) — второй раз не переключаем
            Restore(false);
            _phase = Phase.Counting;
        }

        private float StartAt => _forcedStart ?? Math.Max(_cfg.StartMinute * 60f, _notBefore);

        private void LogErr(string msg)
        {
            if (Time.realtimeSinceStartup - _lastErrLog < 30f) return;
            _lastErrLog = Time.realtimeSinceStartup;
            Log.Warn(msg);
        }

        // ---------------- Тик ----------------

        private void Tick()
        {
            try
            {
                if (!Round.IsStarted) return;
                if (_phase == Phase.Idle) _phase = Phase.Counting;
                if (_phase == Phase.Cancelled) return;

                if (_phase == Phase.Counting)
                {
                    if (_forcedStart == null && _cfg.MinPlayers > 0 && Player.List.Count(p => p.SteamId != 0) < _cfg.MinPlayers)
                    {
                        // игроков мало — ждём; когда наберутся, будет хотя бы минута с предупреждениями
                        _notBefore = Round.ElapsedTime + 61f;
                        _warned.RemoveWhere(w => w <= 61);   // отсчёт сдвинулся — ближние предупреждения покажем заново
                        return;
                    }
                    float left = StartAt - Round.ElapsedTime;
                    foreach (var w in _cfg.WarningSeconds.OrderByDescending(x => x))
                    {
                        if (left <= w && left > w - 1.5f && _warned.Add(w)) Warn(w);
                    }
                    if (left <= 0f) Activate();
                    return;
                }

                if (_phase == Phase.Active) Gas();
            }
            catch (Exception e) { LogErr($"[Decont] {e}"); }
        }

        private static string Plural(int n, string one, string few, string many)
        {
            int m10 = n % 10, m100 = n % 100;
            if (m10 == 1 && m100 != 11) return one;
            if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
            return many;
        }

        private static string TimeText(int sec) =>
            sec >= 60 && sec % 60 == 0 ? $"{sec / 60} {Plural(sec / 60, "минуту", "минуты", "минут")}" : $"{sec} {Plural(sec, "секунду", "секунды", "секунд")}";

        private void Warn(int sec)
        {
            Server.Broadcast(_cfg.Prefix + _cfg.WarningText.Replace("{time}", TimeText(sec)), _cfg.BroadcastSeconds);
            Log.Info($"[Decont] до деконтаминации ЛЗС {sec} с");
            if (sec <= 60)
                foreach (var p in Player.List.Where(p => p.IsValid && !IsDead(p) && p.Zone() == ZoneType.Light))
                    p.ShowMessage(_cfg.PersonalWarning.Replace("{time}", TimeText(sec)));
        }

        private static bool IsDead(Player p) => p.IsDead || p.Team == Team.None || p.Team == Team.Spectator || p.Team == Team.Watcher;

        // ---------------- Начало ----------------

        private (List<Door> Checkpoints, List<Door> LczDoors) SelectDoors()
        {
            var cps = new List<Door>(); var lcz = new List<Door>();
            foreach (var d in Door.List)
            {
                try
                {
                    if (d == null || !d.IsValid || d.IsDestroyed) continue;
                    bool isCp = string.Equals(d.AccessLevel, "Checkpoint", StringComparison.OrdinalIgnoreCase);
                    bool fromRoot = d.ZoneFromRoot;
                    if (isCp)
                    {
                        // КПП лёгкой зоны: по корню сцены (Map_light_zone/LCZ_CheckpointA); запасной вариант — лампы ЛЗС рядом
                        if (fromRoot ? d.Zone == ZoneType.Light : Map.ZonesNear(d.Position, _cfg.CheckpointRadius).Contains(ZoneType.Light)) cps.Add(d);
                        continue;
                    }
                    if (_cfg.SkipAirlocks && (d.IsAirlock || string.Equals(d.AccessLevel, "Airlock", StringComparison.OrdinalIgnoreCase))) continue;
                    if (d.Zone == ZoneType.Light) lcz.Add(d);
                }
                catch { }
            }
            return (cps, lcz);
        }

        private void Activate()
        {
            _phase = Phase.Active;
            _activeSince = Time.time;
            if (Map.LightCount == 0) Log.Warn("[Decont] на карте не найдено ни одной лампы с зоной — определить ЛЗС нельзя, газ не сработает");

            var (cps, lcz) = SelectDoors();
            int closed = 0, opened = 0;
            if (_cfg.LockCheckpoints)
                foreach (var d in cps)
                {
                    try { _touchedDoors.Add((d, d.IsOpen, d.IsLocked)); if (d.IsOpen) d.Close(); d.Lock(); closed++; }
                    catch (Exception e) { Log.Warn($"[Decont] КПП {d?.Name}: {e.Message}"); }
                }
            if (_cfg.OpenAndLockLczDoors)
                foreach (var d in lcz)
                {
                    try { _touchedDoors.Add((d, d.IsOpen, d.IsLocked)); if (!d.IsOpen) d.Open(); d.Lock(); opened++; }
                    catch (Exception e) { Log.Warn($"[Decont] дверь {d?.Name}: {e.Message}"); }
                }

            // в раунд с событием «Блэкаут» свет уже выключен игрой — переключение его бы ВКЛЮЧИЛО
            if (_cfg.LightsOff && !Map.IsBlackoutRound)
            {
                var targets = Player.List.Where(p => p.IsValid && p.SteamId != 0).ToList();
                Map.ToggleZoneLights(ZoneType.Light, targets, out var reached);
                foreach (var p in reached) _lightsOffFor.Add(p);
            }

            Server.Broadcast(_cfg.Prefix + _cfg.StartText, _cfg.BroadcastSeconds);
            foreach (var p in Player.List.Where(p => p.IsValid && !IsDead(p) && p.Zone() == ZoneType.Light)) p.ShowMessage(_cfg.PersonalInside);
            Log.Info($"[Decont] ДЕКОНТАМИНАЦИЯ ЛЗС: КПП закрыто {closed}, дверей ЛЗС открыто {opened}, лифтов из ЛЗС заблокировано {(_cfg.LockElevators ? Map.Elevators.Count(e => e.Touches(ZoneType.Light)) : 0)}");
        }

        private void OnJoined(JoinedEventArgs e)
        {
            // зашёл во время деконтаминации — пусть тоже видит тёмную ЛЗС
            if (_phase != Phase.Active || !_cfg.LightsOff || Map.IsBlackoutRound) return;
            var p = e.Player;
            // ждём дольше: клиент должен успеть загрузить карту, иначе переключение потеряется, а возврат потом погасит свет
            Timing.CallDelayed(10f, () =>
            {
                if (_phase != Phase.Active || !p.IsValid || _lightsOffFor.Contains(p)) return;
                Map.ToggleZoneLights(ZoneType.Light, new[] { p }, out var reached);
                foreach (var r in reached) _lightsOffFor.Add(r);
            });
        }

        private bool BlockElevator(MapElevator el, Player p)
        {
            if (_phase != Phase.Active || !_cfg.LockElevators || !el.Touches(ZoneType.Light)) return false;
            if (p != null && (!_elevMsgAt.TryGetValue(p, out var t) || Time.time - t > 3f)) { _elevMsgAt[p] = Time.time; p.ShowMessage(_cfg.ElevatorBlocked); }
            return true;
        }

        // ---------------- Газ ----------------

        private void Gas()
        {
            float since = Time.time - _activeSince;
            float mult = 1f + Math.Max(0f, _cfg.DamageRampPerSecond) * since;
            foreach (var p in Player.List.ToList())
            {
                try
                {
                    if (!p.IsValid || IsDead(p) || p.Zone() != ZoneType.Light) continue;
                    float dmg = Math.Max(_cfg.DamagePerSecond, p.MaxHealth * _cfg.PercentMaxHpPerSecond / 100f) * mult;
                    p.Damage(Math.Max(1, (int)Math.Round(dmg)), "Деконтаминация");
                }
                catch (Exception e) { LogErr($"[Decont] газ {p?.Nickname}: {e.Message}"); }
            }
        }

        // ---------------- Возврат ----------------

        /// <param name="lights">вернуть свет (переключить обратно). false — в начале раунда: игра уже включила весь свет сама.</param>
        private void Restore(bool lights)
        {
            int doors = 0;
            foreach (var (d, wasOpen, wasLocked) in _touchedDoors)
            {
                try
                {
                    if (d == null || !d.IsValid) continue;
                    // сначала положение (SetOpen на сервере работает и у запертой), потом замок как был
                    if (!d.IsDestroyed && d.IsOpen != wasOpen) d.IsOpen = wasOpen;
                    if (d.IsLocked != wasLocked) d.IsLocked = wasLocked;
                    doors++;
                }
                catch (Exception e) { LogErr($"[Decont] возврат двери: {e.Message}"); }
            }
            _touchedDoors.Clear();

            int lightsBack = 0;
            if (lights)
            {
                var back = _lightsOffFor.Where(p => p.IsValid).ToList();
                if (back.Count > 0) { Map.ToggleZoneLights(ZoneType.Light, back, out var reached); lightsBack = reached.Count; }
            }
            _lightsOffFor.Clear();
            _elevMsgAt.Clear();

            if (_phase == Phase.Active || doors > 0) Log.Info($"[Decont] всё возвращено: дверей {doors}, свет возвращён {lightsBack} игрокам");
            _phase = Phase.Idle; _forcedStart = null; _notBefore = 0f; _warned.Clear();
        }

        // ---------------- Команда ----------------

        private void RegisterCommand()
        {
            CommandManager.Register(new SimpleCommand("decont", "decont — статус | start [сек] | stop | doors | zone <игрок>", (string[] a, out string r) =>
            {
                var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
                switch (sub)
                {
                    case "start":
                    {
                        if (!Round.IsStarted) { r = "раунд не идёт"; return false; }
                        if (_phase == Phase.Active) { r = "деконтаминация уже идёт"; return false; }
                        int sec = a.Length > 1 && int.TryParse(a[1], out var s) ? Math.Max(0, s) : 30;
                        _forcedStart = Round.ElapsedTime + sec; _phase = Phase.Counting; _warned.Clear();
                        Round.SessionData.TryRemove(CancelKey, out _);
                        foreach (var w in _cfg.WarningSeconds) if (w > sec) _warned.Add(w);   // «5 минут» не показываем, если до старта 30 с
                        r = $"деконтаминация ЛЗС через {sec} с"; return true;
                    }
                    case "stop":
                        if (!Round.IsStarted) { r = "раунд не идёт"; return false; }
                        Restore(true); _phase = Phase.Cancelled;
                        Round.SessionData[CancelKey] = true;
                        r = "деконтаминация отменена до конца раунда, двери и свет возвращены"; return true;
                    case "doors":
                    {
                        var (cps, lcz) = SelectDoors();
                        var el = Map.Elevators;
                        r = $"ламп с зоной на карте: {Map.LightCount}\n" +
                            $"КПП ЛЗС (закроются): {cps.Count}\n  " + string.Join("\n  ", cps.Select(d => $"{d.RootName}/{d.Name} [{Fmt(d.Position)}] {(d.IsOpen ? "открыта" : "закрыта")}{(d.IsLocked ? ", заперта" : "")}")) +
                            $"\nдвери ЛЗС (откроются): {lcz.Count} (по корню сцены: {lcz.Count(d => d.ZoneFromRoot)}, по лампам: {lcz.Count(d => !d.ZoneFromRoot)})" +
                            $"\nблэкаут-раунд: {(Map.IsBlackoutRound ? "да — свет трогать не будем" : "нет")}" +
                            $"\nлифты: {el.Count}\n  " + string.Join("\n  ", el.Select(e => e + (e.Touches(ZoneType.Light) ? "  ← заблокируется" : "")));
                        return true;
                    }
                    case "zone":
                    {
                        if (a.Length < 2) { r = "decont zone <игрок>"; return false; }
                        var p = Player.Find(a[1]); if (p == null) { r = "игрок не найден"; return false; }
                        r = $"{p.Nickname}: зона {p.Zone()} [{Fmt(p.Position)}]"; return true;
                    }
                    default:
                    {
                        string st = _phase switch
                        {
                            Phase.Active => $"ИДЁТ {Time.time - _activeSince:0} с",
                            Phase.Cancelled => "отменена в этом раунде",
                            _ => Round.IsStarted ? $"через {Math.Max(0, StartAt - Round.ElapsedTime):0} с" : "ждёт начала раунда",
                        };
                        r = $"деконтаминация ЛЗС: {st}; изменено дверей {_touchedDoors.Count}, свет выключен у {_lightsOffFor.Count}";
                        return true;
                    }
                }
            }));
        }

        private static string Fmt(Vector3 v) => $"{v.x:0} {v.y:0} {v.z:0}";
    }
}
