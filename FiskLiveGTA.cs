using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Collections.Concurrent;
using GTA;
using GTA.Native;
using GTA.Math;

// FiskLiveGTA - Puente entre FiskLive (Node.js) y GTA V Enhanced via ScriptHookVDotNet3
// Levanta un servidor TCP local en el puerto 8421 y escucha comandos JSON simples.
public class FiskLiveGTA : Script
{
    private const int PORT = 8421;

    private TcpListener _server;
    private Thread _serverThread;
    private readonly ConcurrentQueue<string> _commandQueue = new ConcurrentQueue<string>();
    private volatile bool _running;
    private Vehicle _milestoneVehicle; // vehiculo actual del sistema "reemplazar" (ej. cada X likes)

    // ---------- Desafio Monte Chiliad ----------
    private static readonly Vector3 ChiliadSummit = new Vector3(450.718f, 5566.614f, 806.183f);
    private const float ChiliadRadius = 20f; // metros de tolerancia alrededor de la cima
    private const float ChiliadHoldSeconds = 10f;
    private const string FiskLiveStatusUrl = "http://127.0.0.1:8420/api/gta/chiliad-status";

    private bool _chiliadActive;
    private float _chiliadHoldTimer;
    private int _chiliadVictories;
    private string _chiliadLastPhase = "";
    private DateTime _chiliadLastReport = DateTime.MinValue;
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };

    public FiskLiveGTA()
    {
        Tick += OnTick;
        Aborted += OnAborted;
        StartServer();
        GTA.UI.Notification.PostTicker("~g~FiskLive GTA~w~ conectado - puerto " + PORT, false);
    }

    // ---------- Servidor TCP (corre en su propio hilo) ----------

    private void StartServer()
    {
        _running = true;
        _serverThread = new Thread(ServerLoop) { IsBackground = true };
        _serverThread.Start();
    }

    private void ServerLoop()
    {
        try
        {
            _server = new TcpListener(IPAddress.Loopback, PORT);
            _server.Start();

            while (_running)
            {
                using (TcpClient client = _server.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] buffer = new byte[4096];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                        // Soporta varios comandos separados por salto de linea en una sola conexion
                        foreach (string line in msg.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string trimmed = line.Trim();
                            if (trimmed.Length > 0) _commandQueue.Enqueue(trimmed);
                        }
                    }

                    byte[] ack = Encoding.UTF8.GetBytes("OK\n");
                    stream.Write(ack, 0, ack.Length);
                }
            }
        }
        catch (SocketException)
        {
            // Servidor detenido (Aborted) - esperado, no hacer nada
        }
        catch (Exception)
        {
            // Cualquier otro error de red, no tirar el script abajo
        }
    }

    // ---------- Loop principal del juego: procesa comandos en cola ----------

    private void OnTick(object sender, EventArgs e)
    {
        // No procesar comandos mientras el juego esta pausado (menu, alt-tab, etc.)
        // Llamar a natives que modifican el mundo durante la pausa puede crashear
        // el juego al despausar. Los comandos quedan en cola y se procesan apenas
        // el juego vuelve a estar activo.
        if (Game.IsPaused) return;

        while (_commandQueue.TryDequeue(out string cmd))
        {
            try
            {
                HandleCommand(cmd);
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~FiskLive error:~w~ " + ex.Message, false);
            }
        }

        if (_chiliadActive)
        {
            UpdateChiliadChallenge();
        }
    }

    private void HandleCommand(string json)
    {
        string action = ExtractValue(json, "action");
        Ped player = Game.Player.Character;

        switch (action)
        {
            case "spawn_vehicle":
                SpawnVehicle(ExtractValue(json, "model") ?? "adder");
                break;

            case "spawn_vehicle_milestone":
                SpawnMilestoneVehicle(ExtractValue(json, "model") ?? "random");
                break;

            case "give_weapon":
                GiveWeapon(ExtractValue(json, "weapon") ?? "WEAPON_PISTOL");
                break;

            case "set_wanted":
                int level = ExtractInt(json, "level", 3);
                if (level < 0) level = 0;
                if (level > 5) level = 5;
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, level, false);
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
                break;

            case "set_health":
                player.Health = ExtractInt(json, "value", 100);
                break;

            case "set_armor":
                player.Armor = ExtractInt(json, "value", 100);
                break;

            case "explode_nearby":
                ExplodeNearby();
                break;

            case "set_weather":
                Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, ExtractValue(json, "type") ?? "THUNDER");
                break;

            case "teleport_random":
                TeleportRandom();
                break;

            case "ragdoll":
                Function.Call(Hash.SET_PED_TO_RAGDOLL, player, 3000, 3000, 0, 1, 1, 0);
                break;

            case "spawn_ped_chaos":
                SpawnChaosPeds(ExtractInt(json, "count", 5));
                break;

            case "chiliad_start":
                _chiliadActive = true;
                _chiliadHoldTimer = 0f;
                _chiliadLastPhase = "";
                GTA.UI.Notification.PostTicker("~g~Desafio Monte Chiliad iniciado~w~", false);
                ReportChiliadStatus("climbing", 0f, -1f);
                break;

            case "chiliad_stop":
                _chiliadActive = false;
                _chiliadHoldTimer = 0f;
                GTA.UI.Notification.PostTicker("~y~Desafio Monte Chiliad detenido~w~", false);
                ReportChiliadStatus("stopped", 0f, -1f);
                break;

            default:
                GTA.UI.Notification.PostTicker("~y~FiskLive:~w~ accion desconocida '" + action + "'", false);
                break;
        }
    }

    // ---------- Acciones ----------

    // Lista de vehiculos "divertidos" para cuando piden uno random, mezclando categorias
    private static readonly string[] RandomVehicles = new string[]
    {
        // Autos deportivos / muscle
        "adder", "zentorno", "t20", "osiris", "entityxf", "cheetah", "banshee", "sultanrs",
        // Autos chicos / raros
        "comet2", "brioso", "blista", "panto", "issi2", "duneloader",
        // Motos y bicis
        "bati801", "akuma", "sanchez", "bmx", "cruiser", "scorcher",
        // Aviones y helicopteros
        "velum", "mallard", "luxor", "buzzard", "maverick", "cargoplane",
        // Barcos y lanchas
        "jetmax", "speeder", "dinghy", "tug", "toro",
        // Utilitarios/caoticos
        "brutus", "trophytruck", "monster", "dune"
    };

    private void SpawnVehicle(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) ||
            modelName.Equals("random", StringComparison.OrdinalIgnoreCase) ||
            modelName.Equals("aleatorio", StringComparison.OrdinalIgnoreCase))
        {
            Random pick = new Random();
            modelName = RandomVehicles[pick.Next(RandomVehicles.Length)];
        }

        Model model = new Model(modelName);
        model.Request(1000);
        if (!model.IsLoaded)
        {
            GTA.UI.Notification.PostTicker("~r~No se pudo cargar el modelo:~w~ " + modelName, false);
            return;
        }

        Vector3 pos = Game.Player.Character.Position + Game.Player.Character.ForwardVector * 6f;
        // Aviones y helicopteros los ponemos un poco mas arriba para que no choquen contra el piso al aparecer
        Vector3 spawnPos = pos;

        Vehicle veh = World.CreateVehicle(model, spawnPos);
        if (veh != null)
        {
            // Solo "pegamos al piso" los vehiculos que van sobre ruedas.
            // Aviones, helicopteros y barcos se ven raros o quedan trabados si forzamos esto.
            VehicleClass vClass = veh.ClassType;
            bool isGroundVehicle = vClass != VehicleClass.Planes
                && vClass != VehicleClass.Helicopters
                && vClass != VehicleClass.Boats;

            if (isGroundVehicle)
            {
                veh.PlaceOnGround();
            }
            else
            {
                // Los levantamos un poco para que no aparezcan enterrados en el piso
                veh.Position = spawnPos + new Vector3(0, 0, 3f);
            }

            GTA.UI.Notification.PostTicker("~g~Vehiculo spawneado:~w~ " + modelName, false);
        }
        model.MarkAsNoLongerNeeded();
    }

    // Igual que SpawnVehicle, pero borra el vehiculo anterior de este sistema
    // (si existe, esté donde esté, lo esten usando o no) y sube al jugador
    // automaticamente al nuevo. Pensado para efectos tipo "cada 100 likes
    // cambia el auto".
    private void SpawnMilestoneVehicle(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) ||
            modelName.Equals("random", StringComparison.OrdinalIgnoreCase) ||
            modelName.Equals("aleatorio", StringComparison.OrdinalIgnoreCase))
        {
            Random pick = new Random();
            modelName = RandomVehicles[pick.Next(RandomVehicles.Length)];
        }

        Model model = new Model(modelName);
        model.Request(1000);
        if (!model.IsLoaded)
        {
            GTA.UI.Notification.PostTicker("~r~No se pudo cargar el modelo:~w~ " + modelName, false);
            return;
        }

        Ped player = Game.Player.Character;
        Vector3 spawnPos = player.Position + player.ForwardVector * 6f;

        // Borramos el vehiculo anterior de este sistema, si sigue existiendo
        if (_milestoneVehicle != null && _milestoneVehicle.Exists())
        {
            _milestoneVehicle.Delete();
        }

        Vehicle veh = World.CreateVehicle(model, spawnPos);
        if (veh != null)
        {
            VehicleClass vClass = veh.ClassType;
            bool isGroundVehicle = vClass != VehicleClass.Planes
                && vClass != VehicleClass.Helicopters
                && vClass != VehicleClass.Boats;

            if (isGroundVehicle)
            {
                veh.PlaceOnGround();
            }
            else
            {
                veh.Position = spawnPos + new Vector3(0, 0, 3f);
            }

            // Subimos al jugador directo al vehiculo nuevo (asiento del conductor)
            Function.Call(Hash.SET_PED_INTO_VEHICLE, player, veh, -1);

            _milestoneVehicle = veh;
            GTA.UI.Notification.PostTicker("~g~Nuevo vehiculo:~w~ " + modelName, false);
        }
        model.MarkAsNoLongerNeeded();
    }

    // Pool de armas variadas para cuando piden una random
    private static readonly string[] RandomWeapons = new string[]
    {
        "WEAPON_PISTOL", "WEAPON_COMBATPISTOL", "WEAPON_MICROSMG", "WEAPON_SMG",
        "WEAPON_ASSAULTRIFLE", "WEAPON_CARBINERIFLE", "WEAPON_PUMPSHOTGUN",
        "WEAPON_SAWNOFFSHOTGUN", "WEAPON_MINIGUN", "WEAPON_RPG", "WEAPON_GRENADELAUNCHER",
        "WEAPON_SNIPERRIFLE", "WEAPON_HEAVYSNIPER", "WEAPON_MOLOTOV", "WEAPON_GRENADE",
        "WEAPON_STICKYBOMB", "WEAPON_KATANA", "WEAPON_BAT", "WEAPON_KNIFE",
        "WEAPON_FIREEXTINGUISHER", "WEAPON_FLAREGUN", "WEAPON_RAILGUN"
    };

    private void GiveWeapon(string weaponName)
    {
        try
        {
            string normalized = weaponName.Trim().ToUpperInvariant();

            if (normalized == "RANDOM" || normalized == "ALEATORIO" ||
                normalized == "WEAPON_RANDOM" || normalized == "WEAPON_ALEATORIO")
            {
                Random pick = new Random();
                normalized = RandomWeapons[pick.Next(RandomWeapons.Length)];
            }
            else if (!normalized.StartsWith("WEAPON_"))
            {
                normalized = "WEAPON_" + normalized;
            }

            uint hash = (uint)Game.GenerateHash(normalized);

            // Validamos que el hash corresponda a un arma real antes de festejar.
            // Sin esto, un nombre mal escrito "funciona" sin tirar error pero no entrega nada.
            bool isValid = Function.Call<bool>(Hash.IS_WEAPON_VALID, hash);
            if (!isValid)
            {
                GTA.UI.Notification.PostTicker("~r~Arma no reconocida:~w~ " + weaponName + " (probado como " + normalized + ")", false);
                return;
            }

            WeaponHash weaponHash = (WeaponHash)hash;
            Game.Player.Character.Weapons.Give(weaponHash, 250, true, true);
            GTA.UI.Notification.PostTicker("~g~Arma entregada:~w~ " + normalized, false);
        }
        catch (Exception ex)
        {
            GTA.UI.Notification.PostTicker("~r~Error con el arma:~w~ " + weaponName + " (" + ex.Message + ")", false);
        }
    }

    private void ExplodeNearby()
    {
        Vector3 pos = Game.Player.Character.Position + Game.Player.Character.ForwardVector * 8f;
        Function.Call(Hash.ADD_EXPLOSION, pos.X, pos.Y, pos.Z, 2, 1.5f, true, false, 1.0f);
    }

    private void TeleportRandom()
    {
        Random rnd = new Random();
        Vector3 current = Game.Player.Character.Position;
        Vector3 target = current + new Vector3(rnd.Next(-500, 500), rnd.Next(-500, 500), 0);

        OutputArgument groundZArg = new OutputArgument();
        Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, target.X, target.Y, 1000f, groundZArg, false);
        float groundZ = groundZArg.GetResult<float>();
        if (groundZ <= 0f) groundZ = current.Z;

        Game.Player.Character.Position = new Vector3(target.X, target.Y, groundZ + 1f);
        GTA.UI.Notification.PostTicker("~g~Teletransportado~w~", false);
    }

    private void SpawnChaosPeds(int count)
    {
        Vector3 basePos = Game.Player.Character.Position;
        Random rnd = new Random();

        for (int i = 0; i < count; i++)
        {
            Model model = new Model("g_m_y_ballasout_01");
            model.Request(500);
            if (!model.IsLoaded) continue;

            Vector3 offset = basePos + new Vector3(rnd.Next(-10, 10), rnd.Next(-10, 10), 0);
            Ped ped = World.CreatePed(model, offset);
            if (ped != null)
            {
                Function.Call(Hash.SET_PED_AS_ENEMY, ped, true);
                ped.Weapons.Give(WeaponHash.Pistol, 999, true, true);
                Function.Call(Hash.TASK_COMBAT_PED, ped, Game.Player.Character, 0, 16);
            }
            model.MarkAsNoLongerNeeded();
        }

        GTA.UI.Notification.PostTicker("~g~Caos desatado:~w~ " + count + " enemigos", false);
    }

    // ---------- Desafio Monte Chiliad ----------

    private void UpdateChiliadChallenge()
    {
        Ped player = Game.Player.Character;

        bool playerDown = player.IsDead || Function.Call<bool>(Hash.IS_PLAYER_BEING_ARRESTED, Game.Player, false);
        float distance = player.Position.DistanceTo(ChiliadSummit);
        bool atSummit = distance <= ChiliadRadius;

        string phase;

        if (playerDown)
        {
            _chiliadHoldTimer = 0f;
            phase = "failed";
        }
        else if (atSummit)
        {
            _chiliadHoldTimer += Game.LastFrameTime;
            if (_chiliadHoldTimer >= ChiliadHoldSeconds)
            {
                _chiliadVictories++;
                _chiliadHoldTimer = 0f;
                phase = "victory";
                GTA.UI.Notification.PostTicker("~g~VICTORIA en el Monte Chiliad!~w~ Total: " + _chiliadVictories, false);
            }
            else
            {
                phase = "holding";
            }
        }
        else
        {
            _chiliadHoldTimer = 0f;
            phase = "climbing";
        }

        bool phaseChanged = phase != _chiliadLastPhase;
        bool timeToReport = (DateTime.Now - _chiliadLastReport).TotalMilliseconds >= 500;

        if (phaseChanged || timeToReport)
        {
            ReportChiliadStatus(phase, _chiliadHoldTimer, distance);
            _chiliadLastPhase = phase;
            _chiliadLastReport = DateTime.Now;
        }
    }

    // Le avisa a FiskLive (app de Node) el estado actual del desafio, para que
    // lo muestre en el overlay de OBS. Se manda por HTTP a la app local; si
    // FiskLive no esta corriendo o no responde, simplemente se ignora el error
    // y el desafio sigue funcionando igual adentro del juego.
    private void ReportChiliadStatus(string phase, float holdSeconds, float distance)
    {
        string distanceStr = distance >= 0
            ? distance.ToString("0.0", CultureInfo.InvariantCulture)
            : "null";

        string json = "{\"phase\":\"" + phase + "\""
            + ",\"holdSeconds\":" + holdSeconds.ToString("0.0", CultureInfo.InvariantCulture)
            + ",\"holdTarget\":" + ChiliadHoldSeconds.ToString("0.0", CultureInfo.InvariantCulture)
            + ",\"distance\":" + distanceStr
            + ",\"victories\":" + _chiliadVictories
            + "}";

        Task.Run(async () =>
        {
            try
            {
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _http.PostAsync(FiskLiveStatusUrl, content);
            }
            catch
            {
                // FiskLive no esta corriendo o no responde - no rompe el desafio
            }
        });
    }

    // ---------- Parseo JSON minimo (sin dependencias externas) ----------

    private string ExtractValue(string json, string key)
    {
        string pattern = "\"" + key + "\"";
        int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        int colon = json.IndexOf(':', idx);
        if (colon < 0) return null;

        int start = json.IndexOf('"', colon + 1);
        if (start < 0) return null;

        int end = json.IndexOf('"', start + 1);
        if (end < 0) return null;

        return json.Substring(start + 1, end - start - 1);
    }

    private int ExtractInt(string json, string key, int fallback)
    {
        string pattern = "\"" + key + "\"";
        int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return fallback;

        int colon = json.IndexOf(':', idx);
        if (colon < 0) return fallback;

        int end = json.IndexOfAny(new[] { ',', '}' }, colon);
        if (end < 0) end = json.Length;

        string numStr = json.Substring(colon + 1, end - colon - 1).Trim();
        int result;
        return int.TryParse(numStr, out result) ? result : fallback;
    }

    // ---------- Cierre limpio ----------

    private void OnAborted(object sender, EventArgs e)
    {
        _running = false;
        try { _server?.Stop(); } catch { /* ignorar */ }
    }
}
