using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Drawing;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private List<(Prop prop, DateTime spawnedAt)> _activeBoulders = new List<(Prop, DateTime)>();

    // Sistema generico de "hace esto dentro de X segundos" - lo usamos para
    // que efectos como la neblina o el apocalipsis se reviertan solos.
    private List<(DateTime fireAt, Action action)> _scheduledActions = new List<(DateTime, Action)>();
    private bool _blindingFogActive;

    private void ScheduleIn(float seconds, Action action)
    {
        _scheduledActions.Add((DateTime.Now.AddSeconds(seconds), action));
    }

    private void ProcessScheduledActions()
    {
        for (int i = _scheduledActions.Count - 1; i >= 0; i--)
        {
            if (DateTime.Now >= _scheduledActions[i].fireAt)
            {
                Action pending = _scheduledActions[i].action;
                _scheduledActions.RemoveAt(i);
                try { pending.Invoke(); } catch { /* no dejar que un efecto roto tire abajo el resto */ }
            }
        }
    }

    // ---------- Desafio Monte Chiliad ----------
    private static readonly Vector3 ChiliadSummit = new Vector3(501.7849f, 5603.8711f, 797.9101f);
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

        if (_activeBoulders.Count > 0)
        {
            CleanupBoulders();
        }

        if (_scheduledActions.Count > 0)
        {
            ProcessScheduledActions();
        }

        if (_blindingFogActive)
        {
            DrawFogOverlay();
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

            case "spawn_boulders":
                SpawnBoulders(ExtractInt(json, "count", 3));
                break;

            case "spawn_giant_balls":
                SpawnGiantBalls(ExtractInt(json, "count", 4));
                break;

            case "car_rain":
                CarRain(ExtractInt(json, "count", 5));
                break;

            case "break_vehicle":
                BreakCurrentVehicle();
                break;

            case "blinding_fog":
                BlindingFog(ExtractInt(json, "seconds", 15));
                break;

            case "apocalypse":
                TriggerApocalypse(ExtractInt(json, "seconds", 30));
                break;

            case "killer_monkeys":
                SpawnKillerMonkeys(ExtractInt(json, "count", 5));
                break;

            case "chiliad_start":
                _chiliadActive = true;
                _chiliadHoldTimer = 0f;
                _chiliadLastPhase = "";
                Function.Call(Hash.SET_NEW_WAYPOINT, ChiliadSummit.X, ChiliadSummit.Y);
                GTA.UI.Notification.PostTicker("~g~Desafio Monte Chiliad iniciado~w~ - seguí el marcador del mapa", false);
                ReportChiliadStatus("climbing", 0f, -1f);
                break;

            case "chiliad_stop":
                _chiliadActive = false;
                _chiliadHoldTimer = 0f;
                Function.Call(Hash.SET_WAYPOINT_OFF);
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
    // Vehiculos terrestres para el pool random (autos, motos, bicis, buses,
    // cuatriciclos, militares terrestres, utilitarios de caos)
    private static readonly string[] LandVehicles = new string[]
    {
        // Autos deportivos / muscle
        "adder", "zentorno", "t20", "osiris", "entityxf", "cheetah", "banshee", "sultanrs",
        // Autos chicos / raros
        "comet2", "brioso", "blista", "panto", "issi2", "dloader",
        // Motos y bicis
        "bati", "akuma", "sanchez", "bmx", "cruiser", "scorcher",
        // Buses
        "bus", "coach",
        // Cuatriciclos
        "blazer", "blazer2", "blazer3",
        // Militares terrestres / "humvees"
        "insurgent", "insurgent2", "insurgent3", "menacer",
        // Militares / tanques
        "rhino", "tampa3",
        // Utilitarios/caoticos
        "brutus", "trophytruck", "monster", "dune"
    };

    // Aviones, helicopteros y barcos - el pool "especial" que sale 1 de
    // cada 20 veces en vez del pool terrestre normal.
    private static readonly string[] AirWaterVehicles = new string[]
    {
        "velum", "stunt", "luxor", "buzzard", "maverick", "cargoplane",
        "jetmax", "speeder", "dinghy", "tug", "toro"
    };

    // Cuenta cuantas veces se pidio un vehiculo random, para que 1 de cada
    // 20 salga del pool de aviones/barcos en vez del terrestre.
    private int _vehicleSpawnCounter = 0;

    private string PickRandomVehicleModel()
    {
        Random rnd = new Random();
        _vehicleSpawnCounter++;

        if (_vehicleSpawnCounter % 20 == 0)
        {
            return AirWaterVehicles[rnd.Next(AirWaterVehicles.Length)];
        }

        return LandVehicles[rnd.Next(LandVehicles.Length)];
    }

    private void SpawnVehicle(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) ||
            modelName.Equals("random", StringComparison.OrdinalIgnoreCase) ||
            modelName.Equals("aleatorio", StringComparison.OrdinalIgnoreCase))
        {
            modelName = PickRandomVehicleModel();
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
            modelName = PickRandomVehicleModel();
        }

        Model model = new Model(modelName);
        model.Request(1000);
        if (!model.IsLoaded)
        {
            GTA.UI.Notification.PostTicker("~r~No se pudo cargar el modelo:~w~ " + modelName, false);
            return;
        }

        Ped player = Game.Player.Character;

        // Guardamos referencia al vehiculo que el jugador tiene AHORA, sea el
        // que nosotros mismos le dimos antes o uno robado/encontrado en el
        // mundo. Lo borramos recien despues de subirlo al nuevo, para que el
        // cambio sea seguro incluso si esta manejando en movimiento.
        bool playerCurrentlyDriving = player.IsInVehicle();
        Vehicle vehicleToRemove = playerCurrentlyDriving ? player.CurrentVehicle : _milestoneVehicle;

        // Si esta manejando, el auto nuevo aparece EXACTAMENTE en su posicion
        // actual (no adelantado), para que el cambio se sienta como un
        // "reskin" instantaneo. Si esta a pie, SIEMPRE usamos su posicion
        // actual (nunca la del auto viejo abandonado, que puede estar lejos
        // o en el agua) para no teletransportarlo por sorpresa.
        Vector3 spawnPos = (playerCurrentlyDriving && vehicleToRemove != null && vehicleToRemove.Exists())
            ? vehicleToRemove.Position
            : player.Position + player.ForwardVector * 6f;

        // Guardamos la velocidad actual (direccion + magnitud) para pasarsela
        // al vehiculo nuevo y que no se sienta como un frenazo brusco. Solo
        // tiene sentido si esta manejando ahora mismo ese vehiculo.
        Vector3 previousVelocity = (playerCurrentlyDriving && vehicleToRemove != null && vehicleToRemove.Exists())
            ? vehicleToRemove.Velocity
            : Vector3.Zero;

        // Idem con el heading: si esta a pie, usamos hacia donde mira el
        // jugador, no la orientacion de un auto abandonado en otro lado.
        float previousHeading = (playerCurrentlyDriving && vehicleToRemove != null && vehicleToRemove.Exists())
            ? vehicleToRemove.Heading
            : player.Heading;

        Vehicle veh = World.CreateVehicle(model, spawnPos);
        if (veh != null)
        {
            veh.Heading = previousHeading;

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

            // Le pasamos la velocidad que traia el auto anterior, para que el
            // cambio se sienta continuo en vez de arrancar frenado en seco.
            if (previousVelocity.Length() > 0.5f)
            {
                veh.Velocity = previousVelocity;
            }

            // Subimos al jugador directo al vehiculo nuevo (asiento del conductor).
            // Esto lo saca automaticamente de cualquier vehiculo en el que este,
            // incluso si esta andando.
            Function.Call(Hash.SET_PED_INTO_VEHICLE, player, veh, -1);

            // Recien ahora borramos el vehiculo anterior, ya vacio (evita
            // borrar un auto mientras el jugador todavia esta adentro)
            if (vehicleToRemove != null && vehicleToRemove.Exists() && vehicleToRemove != veh)
            {
                vehicleToRemove.Delete();
            }

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

    // Piedras/rocas gigantes con fisica real: caen desde arriba y ruedan,
    // pueden aplastar/lastimar al jugador y a lo que encuentren en el camino.
    // Se limpian solas a los 20 segundos para no acumular basura en el mapa.
    private void SpawnBoulders(int count)
    {
        Ped player = Game.Player.Character;
        Model model = new Model("prop_test_boulder_04");
        model.Request(1000);

        if (!model.IsLoaded)
        {
            GTA.UI.Notification.PostTicker("~r~No se pudo cargar la roca~w~", false);
            return;
        }

        Random rnd = new Random();
        for (int i = 0; i < count; i++)
        {
            Vector3 offset = new Vector3(
                rnd.Next(-8, 8),
                rnd.Next(-8, 8),
                18f + rnd.Next(0, 8)); // bien arriba, para que caiga con fuerza

            Vector3 spawnPos = player.Position + offset;
            Prop boulder = World.CreateProp(model, spawnPos, true, false);

            if (boulder != null)
            {
                // Un prop dinamico recien creado a veces queda "dormido" y
                // no empieza a caer solo. Esto lo activa a la fuerza.
                Function.Call(Hash.ACTIVATE_PHYSICS, boulder.Handle);
                _activeBoulders.Add((boulder, DateTime.Now));
            }
        }

        model.MarkAsNoLongerNeeded();
        GTA.UI.Notification.PostTicker("~r~¡Cuidado!~w~ Rocas gigantes cayendo", false);
    }

    // Pelotas gigantes esfericas de verdad (no rocas anguladas). Mismo
    // sistema que las rocas: caen desde arriba con fisica real y pueden
    // golpear/aplastar al jugador y lo que encuentren en el camino.
    private void SpawnGiantBalls(int count)
    {
        Ped player = Game.Player.Character;
        Model model = new Model("prop_beachball_02");
        model.Request(1000);

        if (!model.IsLoaded)
        {
            GTA.UI.Notification.PostTicker("~r~No se pudo cargar la pelota~w~", false);
            return;
        }

        Random rnd = new Random();
        for (int i = 0; i < count; i++)
        {
            Vector3 offset = new Vector3(
                rnd.Next(-8, 8),
                rnd.Next(-8, 8),
                18f + rnd.Next(0, 8));

            Vector3 spawnPos = player.Position + offset;
            Prop ball = World.CreateProp(model, spawnPos, true, false);

            if (ball != null)
            {
                Function.Call(Hash.ACTIVATE_PHYSICS, ball.Handle);
                _activeBoulders.Add((ball, DateTime.Now));
            }
        }

        model.MarkAsNoLongerNeeded();
        GTA.UI.Notification.PostTicker("~r~¡Pelotas gigantes cayendo!~w~", false);
    }

    private void CleanupBoulders()
    {
        for (int i = _activeBoulders.Count - 1; i >= 0; i--)
        {
            var entry = _activeBoulders[i];
            bool expired = (DateTime.Now - entry.spawnedAt).TotalSeconds > 20;

            if (!entry.prop.Exists() || expired)
            {
                if (entry.prop.Exists()) entry.prop.Delete();
                _activeBoulders.RemoveAt(i);
            }
        }
    }

    // El vehiculo actual se rompe en vivo: revienta las ruedas, se le caen
    // las puertas, se estrellan los vidrios. No lo destruye del todo (no
    // explota), asi que no es letal, pero queda imposible de manejar bien.
    private void BreakCurrentVehicle()
    {
        Ped player = Game.Player.Character;
        if (!player.IsInVehicle())
        {
            GTA.UI.Notification.PostTicker("~y~No estas en ningun vehiculo para desarmar~w~", false);
            return;
        }

        Vehicle veh = player.CurrentVehicle;

        for (int i = 0; i < 8; i++)
        {
            Function.Call(Hash.SET_VEHICLE_TYRE_BURST, veh, i, true, 1000f);
            Function.Call(Hash.SET_VEHICLE_DOOR_BROKEN, veh, i, true);
            Function.Call(Hash.SMASH_VEHICLE_WINDOW, veh, i);
        }

        veh.EngineHealth = 50f;
        veh.BodyHealth = 50f;

        GTA.UI.Notification.PostTicker("~r~¡Tu vehiculo se desarma en pedazos!~w~", false);
    }

    // Neblina super espesa: clima de niebla + un velo gris solido tapando
    // toda la pantalla, dibujado cada frame mientras dure. Mucho mas
    // efectivo que solo cambiar el clima. Vuelve todo a la normalidad
    // solo despues de X segundos.
    private void BlindingFog(int seconds)
    {
        Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, "FOGGY");
        Function.Call(Hash.SET_WIND_SPEED, 0f); // sin viento, para que no se disperse
        _blindingFogActive = true;

        GTA.UI.Notification.PostTicker("~b~¡Neblina cegadora!~w~ No se ve nada por " + seconds + "s", false);

        ScheduleIn(seconds, () =>
        {
            _blindingFogActive = false;
            Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, "CLEAR");
            GTA.UI.Notification.PostTicker("~g~La neblina se disipa~w~", false);
        });
    }

    // Dibuja un velo gris casi opaco tapando toda la pantalla. Se llama
    // todos los frames mientras _blindingFogActive sea true.
    private void DrawFogOverlay()
    {
        var overlay = new GTA.UI.ContainerElement(
            new PointF(0, 0),
            new SizeF(GTA.UI.Screen.Width, GTA.UI.Screen.Height),
            Color.FromArgb(235, 215, 215, 215)); // gris claro, casi opaco
        overlay.Draw();
    }

    // Combo de caos por X segundos: tormenta, busqueda maxima, y explosiones
    // + enemigos cada 4 segundos. Al terminar, todo vuelve a la normalidad.
    private void TriggerApocalypse(int seconds)
    {
        Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, "THUNDER");
        Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, 5, false);
        Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);

        GTA.UI.Notification.PostTicker("~r~¡APOCALIPSIS DESATADO!~w~ " + seconds + " segundos de caos total", false);

        int pulses = seconds / 4;
        for (int p = 1; p <= pulses; p++)
        {
            ScheduleIn(p * 4, () =>
            {
                ExplodeNearby();
                SpawnChaosPeds(3);
            });
        }

        ScheduleIn(seconds, () =>
        {
            Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, "CLEAR");
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, 0, false);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
            GTA.UI.Notification.PostTicker("~g~El apocalipsis termino... por ahora~w~", false);
        });
    }

    // Chimpances hostiles atacando al jugador. Los animales no pelean igual
    // que los humanos (no usan armas), pero con SET_PED_AS_ENEMY y
    // TASK_COMBAT_PED van a perseguir y atacar cuerpo a cuerpo.
    private void SpawnKillerMonkeys(int count)
    {
        Ped player = Game.Player.Character;
        Model model = new Model("a_c_chimp");
        model.Request(1000);

        if (!model.IsLoaded)
        {
            GTA.UI.Notification.PostTicker("~r~No se pudo cargar el mono~w~", false);
            return;
        }

        Random rnd = new Random();
        for (int i = 0; i < count; i++)
        {
            Vector3 offset = player.Position + new Vector3(rnd.Next(-10, 10), rnd.Next(-10, 10), 0);
            Ped monkey = World.CreatePed(model, offset);
            if (monkey != null)
            {
                // Los animales tienen instinto de huida programado por
                // defecto. Sin esto, SET_PED_AS_ENEMY solo no alcanza y
                // salen corriendo en vez de atacar.
                Function.Call(Hash.TASK_SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, monkey, true);
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, monkey, 0, false);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, monkey, 17, true);

                Function.Call(Hash.SET_PED_AS_ENEMY, monkey, true);
                Function.Call(Hash.TASK_COMBAT_PED, monkey, player, 0, 16);
            }
        }

        model.MarkAsNoLongerNeeded();
        GTA.UI.Notification.PostTicker("~r~¡Monos asesinos sueltos!~w~", false);
    }

    // ---------- Desafio Monte Chiliad ----------

    private void UpdateChiliadChallenge()
    {
        // Circulo visual en el mundo, marcando la zona de la cima. Se dibuja
        // todos los frames mientras el desafio esta activo (asi funciona
        // DRAW_MARKER en GTA, hay que redibujarlo cada frame).
        DrawSummitMarker();

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
                TeleportFarFromChiliad();
            }
            else
            {
                phase = "holding";
                float remaining = ChiliadHoldSeconds - _chiliadHoldTimer;
                GTA.UI.Screen.ShowSubtitle("~y~Aguantá parado: ~w~" + remaining.ToString("0.0") + "s", 100);
            }
        }
        else
        {
            _chiliadHoldTimer = 0f;
            phase = "climbing";
        }

        bool phaseChanged = phase != _chiliadLastPhase;
        bool timeToReport = (DateTime.Now - _chiliadLastReport).TotalMilliseconds >= 500;

        // Si acaba de fallar (murio/lo atraparon) o de ganar (y lo mandamos
        // lejos), reponemos el marcador de guia para el proximo intento.
        if (phaseChanged && (phase == "failed" || phase == "victory"))
        {
            Function.Call(Hash.SET_NEW_WAYPOINT, ChiliadSummit.X, ChiliadSummit.Y);
        }

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
    // Dibuja un circulo/haz de luz en la cima, para que se vea claramente
    // la zona objetivo del desafio. Hay que llamarlo todos los frames
    // mientras el desafio este activo (asi funciona DRAW_MARKER en GTA).
    private void DrawSummitMarker()
    {
        Function.Call(
            Hash.DRAW_MARKER,
            1, // tipo: cilindro con haz de luz hacia arriba
            ChiliadSummit.X, ChiliadSummit.Y, ChiliadSummit.Z - 1f,
            0f, 0f, 0f, // direccion
            0f, 0f, 0f, // rotacion
            ChiliadRadius * 2f, ChiliadRadius * 2f, 4f, // escala (diametro x2, alto)
            255, 190, 40, 130, // color ambar semi-transparente
            false, true, 2, false, (string)null, (string)null, false);
    }

    // Punto fijo de reinicio: entrada del aeropuerto de Los Santos.
    // Coordenadas sacadas directo del juego con el trainer.
    private static readonly Vector3 AirportRestart = new Vector3(-1033.8783f, -2730.7104f, 13.7566f);

    // Al ganar, mandamos al jugador al aeropuerto (no simplemente reiniciamos
    // el contador dejandolo parado ahi, para que el desafio real sea volver
    // a subir desde cero cada vez).
    private void TeleportFarFromChiliad()
    {
        Game.Player.Character.Position = AirportRestart;
    }

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
