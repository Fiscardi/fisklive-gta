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

    // Lista separada para las pelotas gigantes: a diferencia de las rocas,
    // el objeto prop_juicestand no responde bien a la fisica nativa del
    // juego (queda flotando), asi que le programamos la caida a mano.
    // "landingZ" es la altura real de piso (tomada de donde esta parado el
    // jugador al spawnear), NO se recalcula por pelota en cada frame: eso
    // era el bug original, porque GET_GROUND_Z_FOR_3D_COORD sondeaba desde
    // la posicion de CADA pelota, y si su X/Y caia justo arriba de un poste,
    // cerco o muro bajo, el juego detectaba esa superficie como "piso" y la
    // dejaba colgada en el aire en vez de seguir bajando hasta la calle.
    private List<(Prop prop, float velocityZ, DateTime spawnedAt, bool landed, bool hitPlayer, float landingZ)> _fallingBalls
        = new List<(Prop, float, DateTime, bool, bool, float)>();
    private List<(Vehicle vehicle, DateTime spawnedAt)> _activeRainCars = new List<(Vehicle, DateTime)>();
    private List<Ped> _activeHostilePeds = new List<Ped>(); // enemigos de caos + monos, para poder borrarlos de una

    // Lista aparte solo para los monos: los "animales" del juego tienen
    // reacciones de miedo/huida metidas por default, que pueden pisarles
    // la tarea de combate sin avisar. Con esta lista los vamos revisando
    // cada tick para reafirmarles la persecucion y, por las dudas, aplicar
    // dano a mano si llegan a tocar al jugador (asi el peligro es real
    // pase lo que pase con la IA nativa del chimpance).
    private List<Ped> _activeKillerMonkeys = new List<Ped>();
    private DateTime _lastMonkeyReassert = DateTime.MinValue;

    // Agujero negro: succiona vehiculos, peds y al jugador hacia un punto
    // fijo durante X segundos y despues explota. Si el jugador queda muy
    // cerca del centro, muere (elegido asi a proposito).
    private bool _blackHoleActive;
    private Vector3 _blackHoleCenter;
    private DateTime _blackHoleStartedAt;
    private float _blackHoleDurationSeconds;
    private const float BlackHolePullRadius = 30f;
    private const float BlackHoleKillRadius = 2.0f;

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

        if (_fallingBalls.Count > 0)
        {
            try
            {
                UpdateFallingBalls();
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Error en pelotas gigantes:~w~ " + ex.Message, false);
                _fallingBalls.Clear();
            }
        }

        if (_activeKillerMonkeys.Count > 0)
        {
            try
            {
                UpdateKillerMonkeys();
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Error en monos asesinos:~w~ " + ex.Message, false);
                _activeKillerMonkeys.Clear();
            }
        }

        if (_blackHoleActive)
        {
            try
            {
                UpdateBlackHole();
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Error en agujero negro:~w~ " + ex.Message, false);
                _blackHoleActive = false;
            }
        }

        if (_activeRainCars.Count > 0)
        {
            CleanupRainCars();
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

            case "black_hole":
                StartBlackHole(ExtractInt(json, "seconds", 8));
                break;

            case "car_rain":
                CarRain(ExtractInt(json, "seconds", 10));
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

    // ---------- Agujero negro ----------

    private void StartBlackHole(int seconds)
    {
        if (_blackHoleActive)
        {
            GTA.UI.Notification.PostTicker("~y~Ya hay un agujero negro activo~w~", false);
            return;
        }

        Ped player = Game.Player.Character;

        // Lo ubicamos un poco enfrente y a la altura de la cabeza del
        // jugador: se ve bien y da un segundo para reaccionar antes de
        // que la succion empiece a jalar fuerte.
        _blackHoleCenter = player.Position + player.ForwardVector * 6f + new Vector3(0f, 0f, 1.2f);
        _blackHoleStartedAt = DateTime.Now;
        _blackHoleDurationSeconds = Math.Max(3, seconds);
        _blackHoleActive = true;

        GTA.UI.Notification.PostTicker("~p~¡Se abrió un agujero negro!~w~ Corré.", false);
    }

    private void UpdateBlackHole()
    {
        double elapsed = (DateTime.Now - _blackHoleStartedAt).TotalSeconds;

        if (elapsed >= _blackHoleDurationSeconds)
        {
            ExplodeBlackHole();
            return;
        }

        Ped player = Game.Player.Character;

        // "Agujero" dibujado como marcador nativo (no depende de ningun
        // asset de particulas externo, asi que no se rompe entre versiones
        // del juego): una esfera negra que pulsa y va creciendo.
        float growth = 1f + (float)(elapsed / _blackHoleDurationSeconds);
        float pulse = 1.0f + 0.15f * (float)Math.Sin(elapsed * 6.0);
        World.DrawMarker(
            MarkerType.DebugSphere,
            _blackHoleCenter,
            Vector3.Zero,
            Vector3.Zero,
            new Vector3(1.6f, 1.6f, 1.6f) * growth * pulse,
            Color.FromArgb(235, 8, 6, 14));

        // La succion se hace mas fuerte con el tiempo, para que se sienta
        // que el agujero "crece" y cada vez es mas dificil escapar de el.
        float pullStrength = 6f + (float)elapsed * 1.5f;

        PullEntityToward(player, pullStrength);
        if (player.Position.DistanceTo(_blackHoleCenter) < BlackHoleKillRadius)
        {
            player.Health = 0; // el agujero negro tambien mata al jugador si lo atrapa
            GTA.UI.Notification.PostTicker("~r~¡El agujero negro te devoró!~w~", false);
        }

        foreach (Vehicle vehicle in World.GetNearbyVehicles(_blackHoleCenter, BlackHolePullRadius))
        {
            if (vehicle == null || !vehicle.Exists()) continue;
            PullEntityToward(vehicle, pullStrength);
        }

        foreach (Ped ped in World.GetNearbyPeds(_blackHoleCenter, BlackHolePullRadius))
        {
            if (ped == null || !ped.Exists() || ped == player) continue;
            PullEntityToward(ped, pullStrength);
            if (ped.IsAlive && ped.Position.DistanceTo(_blackHoleCenter) < BlackHoleKillRadius)
            {
                ped.Health = 0;
            }
        }
    }

    private void PullEntityToward(Entity entity, float strength)
    {
        if (entity == null || !entity.Exists()) return;

        Vector3 toCenter = _blackHoleCenter - entity.Position;
        float dist = toCenter.Length();
        if (dist < 0.15f) return;

        Vector3 dir = toCenter / dist;

        // Mientras mas cerca del centro, mas fuerte tira (como gravedad
        // real), asi el final se siente como una caida brusca.
        float factor = strength * (1f + (BlackHolePullRadius - Math.Min(dist, BlackHolePullRadius)) / BlackHolePullRadius);

        Function.Call(Hash.APPLY_FORCE_TO_ENTITY, entity.Handle, 3,
            dir.X * factor, dir.Y * factor, dir.Z * factor,
            0f, 0f, 0f, 0, false, true, true, false, true);
    }

    private void ExplodeBlackHole()
    {
        Vector3 center = _blackHoleCenter;

        // Explosion grande en el centro: todo lo que quedo atrapado cerca
        // sale disparado, como si el colapso lo escupiera.
        Function.Call(Hash.ADD_EXPLOSION, center.X, center.Y, center.Z, 2, 2.2f, true, false, 1.0f);

        GTA.UI.Notification.PostTicker("~p~El agujero negro colapsó~w~", false);
        _blackHoleActive = false;
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
                _activeHostilePeds.Add(ped);
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

    // Pelotas gigantes de verdad: el mismo objeto naranja gigante que usan
    // los puestos "Juice Stand" del mapa (el huevo de pascua de GTA V),
    // reutilizado por la comunidad de mods justamente para hacerlo rodar
    // como bola de caos. La fisica nativa del juego no lo mueve (se queda
    // flotando), asi que le programamos la caida a mano, frame por frame.
    private void SpawnGiantBalls(int count)
    {
        Ped player = Game.Player.Character;
        Model model = new Model("prop_juicestand");
        model.Request(1000);

        if (!model.IsLoaded)
        {
            GTA.UI.Notification.PostTicker("~r~No se pudo cargar la pelota~w~", false);
            return;
        }

        // Piso real, calculado UNA sola vez desde donde esta parado el
        // jugador (ahi sabemos con certeza que hay calle/vereda de verdad,
        // no un poste o cerco). Todas las pelotas de esta tanda caen hasta
        // esta altura, sin importar sobre que objeto quede su X/Y.
        float landingZ = player.Position.Z;

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
                // Ademas de descongelarla, forzamos colision activa y la
                // marcamos como "mission entity" para que el juego no la
                // trate como decoracion ambiente y le pise los cambios de
                // posicion que hacemos a mano cada frame.
                Function.Call(Hash.FREEZE_ENTITY_POSITION, ball.Handle, false);
                Function.Call(Hash.SET_ENTITY_COLLISION, ball.Handle, true, true);
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, ball.Handle, true, true);
                _fallingBalls.Add((ball, 0f, DateTime.Now, false, false, landingZ));
            }
        }

        model.MarkAsNoLongerNeeded();
        GTA.UI.Notification.PostTicker("~r~¡Pelotas gigantes cayendo!~w~", false);
    }

    // Lluvia de vehiculos variados (autos, buses, camiones, aviones) cayendo
    // del cielo con fisica real, esparcidos en un area amplia (no todos
    // arriba del jugador), durante X segundos en vez de una sola tanda.
    private static readonly string[] RainVehicleModels = new string[]
    {
        // Autos comunes
        "blista", "asea", "premier", "primo", "panto", "issi2", "dilettante",
        // Buses
        "bus", "coach",
        // Camiones
        "phantom", "packer", "pounder", "mule", "benson", "hauler",
        // Aviones y helicopteros
        "velum", "stunt", "luxor", "buzzard", "maverick", "cargoplane"
    };

    private void CarRain(int seconds)
    {
        GTA.UI.Notification.PostTicker("~r~¡Lluvia de vehiculos!~w~ Durante " + seconds + "s", false);

        const float interval = 0.6f;
        int totalSpawns = (int)(seconds / interval);
        if (totalSpawns < 1) totalSpawns = 1;

        for (int i = 0; i < totalSpawns; i++)
        {
            float delay = i * interval;
            ScheduleIn(delay, SpawnOneRainVehicle);
        }
    }

    private void SpawnOneRainVehicle()
    {
        Ped player = Game.Player.Character;
        Random rnd = new Random();

        string modelName = RainVehicleModels[rnd.Next(RainVehicleModels.Length)];
        Model model = new Model(modelName);
        model.Request(1000);
        if (!model.IsLoaded) return;

        // Area bien amplia alrededor del jugador, no todo concentrado
        // arriba suyo, para que se sienta como lluvia de verdad.
        Vector3 offset = new Vector3(
            rnd.Next(-45, 45),
            rnd.Next(-45, 45),
            22f + rnd.Next(0, 12));

        Vector3 spawnPos = player.Position + offset;
        Vehicle vehicle = World.CreateVehicle(model, spawnPos);

        if (vehicle != null)
        {
            Function.Call(Hash.ACTIVATE_PHYSICS, vehicle.Handle);
            _activeRainCars.Add((vehicle, DateTime.Now));
        }

        model.MarkAsNoLongerNeeded();
    }

    private void CleanupRainCars()
    {
        for (int i = _activeRainCars.Count - 1; i >= 0; i--)
        {
            var entry = _activeRainCars[i];
            bool expired = (DateTime.Now - entry.spawnedAt).TotalSeconds > 25;

            if (!entry.vehicle.Exists() || expired)
            {
                if (entry.vehicle.Exists()) entry.vehicle.Delete();
                _activeRainCars.RemoveAt(i);
            }
        }
    }

    // Borra TODO el caos activo de una: monos, enemigos, rocas, pelotas,
    // autos de la lluvia, agujero negro, y resetea clima/busqueda. Se llama
    // automaticamente al ganar o al morir en el desafio del Monte Chiliad,
    // para arrancar cada intento nuevo limpio.
    private void CleanupAllChaos()
    {
        foreach (Ped ped in _activeHostilePeds)
        {
            if (ped != null && ped.Exists()) ped.Delete();
        }
        _activeHostilePeds.Clear();

        // Los monos ya se borraron arriba (estan tambien en
        // _activeHostilePeds); aca solo vaciamos la lista de seguimiento
        // para que UpdateKillerMonkeys no siga iterando sobre entidades
        // muertas.
        _activeKillerMonkeys.Clear();

        foreach (var entry in _activeBoulders)
        {
            if (entry.prop != null && entry.prop.Exists()) entry.prop.Delete();
        }
        _activeBoulders.Clear();

        foreach (var entry in _fallingBalls)
        {
            if (entry.prop != null && entry.prop.Exists()) entry.prop.Delete();
        }
        _fallingBalls.Clear();

        foreach (var entry in _activeRainCars)
        {
            if (entry.vehicle != null && entry.vehicle.Exists()) entry.vehicle.Delete();
        }
        _activeRainCars.Clear();

        // El agujero negro no tiene props/entidades propias que borrar (es
        // un marcador dibujado + fuerzas), asi que alcanza con apagarlo.
        _blackHoleActive = false;

        // Cancelamos tambien cualquier efecto pendiente (pulsos de
        // apocalipsis, fin de neblina, etc) para que no sigan disparando
        // despues de la limpieza.
        _scheduledActions.Clear();

        Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, "CLEAR");
        Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, 0, false);
        Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
        _blindingFogActive = false;

        GTA.UI.Notification.PostTicker("~b~Se limpio todo el caos~w~", false);
    }

    // Mueve las pelotas gigantes hacia abajo a mano, simulando gravedad,
    // ya que su fisica nativa no responde. Cuando pasan muy cerca del
    // jugador mientras caen, lo tira al piso (una sola vez por pelota).
    private void UpdateFallingBalls()
    {
        Ped player = Game.Player.Character;

        for (int i = _fallingBalls.Count - 1; i >= 0; i--)
        {
            var entry = _fallingBalls[i];

            if (!entry.prop.Exists())
            {
                _fallingBalls.RemoveAt(i);
                continue;
            }

            bool landed = entry.landed;
            bool hitPlayer = entry.hitPlayer;
            float velocityZ = entry.velocityZ;

            if (!landed)
            {
                velocityZ -= 20f * Game.LastFrameTime; // gravedad simulada
                Vector3 pos = entry.prop.Position;
                float newZ = pos.Z + velocityZ * Game.LastFrameTime;

                // Usamos landingZ (piso real, tomado del jugador al spawnear)
                // en vez de volver a sondear el piso bajo la pelota: eso era
                // lo que las dejaba colgadas arriba de postes/cercos.
                if (newZ <= entry.landingZ + 1.0f)
                {
                    newZ = entry.landingZ + 1.0f; // apoyada sobre el piso, sin incrustarse
                    landed = true;

                    // Ahora que ya no esta peleando contra la gravedad,
                    // probamos reactivar la fisica nativa: puede que a
                    // partir de aca si responda a empujones/golpes de
                    // cualquier cosa, no solo de autos con fuerza.
                    Function.Call(Hash.FREEZE_ENTITY_POSITION, entry.prop.Handle, false);
                    Function.Call(Hash.ACTIVATE_PHYSICS, entry.prop.Handle);
                }

                entry.prop.Position = new Vector3(pos.X, pos.Y, newZ);

                if (!hitPlayer && entry.prop.Position.DistanceTo(player.Position) < 2.5f)
                {
                    Function.Call(Hash.SET_PED_TO_RAGDOLL, player, 1500, 1500, 0, 1, 1, 0);
                    player.Health -= 30;
                    hitPlayer = true;
                }
            }

            bool expired = (DateTime.Now - entry.spawnedAt).TotalSeconds > 20;
            if (expired)
            {
                entry.prop.Delete();
                _fallingBalls.RemoveAt(i);
            }
            else
            {
                _fallingBalls[i] = (entry.prop, velocityZ, entry.spawnedAt, landed, hitPlayer, entry.landingZ);
            }
        }
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

                // Refuerzo extra: por default el chimpance no odia al
                // jugador (relacion neutral/miedosa), asi que aunque le
                // saquemos la huida el juego igual lo puede hacer dudar.
                // Poniendolo en el grupo "HATES_PLAYER" y sumando mas
                // atributos de combate, se comporta como un enemigo de
                // verdad en vez de un animal asustado.
                int hatesPlayerGroup = Function.Call<int>(Hash.GET_HASH_KEY, "HATES_PLAYER");
                Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, monkey, hatesPlayerGroup);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, monkey, 46, true); // puede pelear sin arma
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, monkey, 5, true);  // nunca se acobarda
                Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, monkey, 2);          // movimiento agresivo
                Function.Call(Hash.SET_PED_COMBAT_RANGE, monkey, 0);             // pelea cuerpo a cuerpo, de cerca

                // Marcarlo como "mission entity" evita que el juego le
                // vuelva a asignar IA ambiental (de animal comun) por
                // encima de la tarea de combate que le mandamos.
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, monkey, true, true);

                // Le damos un arma cuerpo a cuerpo. Sin arma, la IA de
                // combate de un animal a veces no completa bien la
                // animacion de ataque.
                WeaponHash[] monkeyWeapons = { WeaponHash.Knife, WeaponHash.Hatchet };
                monkey.Weapons.Give(monkeyWeapons[rnd.Next(monkeyWeapons.Length)], 1, true, true);

                Function.Call(Hash.SET_PED_AS_ENEMY, monkey, true);
                Function.Call(Hash.TASK_COMBAT_PED, monkey, player, 0, 16);

                // Ademas del combate, lo mandamos a acercarse directo: un
                // chimpance no tiene garantizado un set de animaciones de
                // combate como el de un humano, asi que esto asegura que
                // arranque moviendose hacia el jugador desde el primer
                // frame, en vez de quedarse parado esperando una logica de
                // combate que puede no disparar nada visible.
                Function.Call(Hash.TASK_GO_TO_ENTITY, monkey.Handle, player.Handle, -1, 1.0f, 3.0f, 1073741824f, 0);

                _activeHostilePeds.Add(monkey);
                _activeKillerMonkeys.Add(monkey);
            }
        }

        model.MarkAsNoLongerNeeded();
        GTA.UI.Notification.PostTicker("~r~¡Monos asesinos sueltos!~w~", false);
    }

    // Reafirma la persecucion cada 2 segundos (por si el juego les pisa la
    // tarea de combate con una reaccion de miedo propia del modelo animal)
    // y aplica dano a mano cuando un mono esta pegado al jugador, para no
    // depender de que el modelo tenga o no una animacion de mordida.
    private void UpdateKillerMonkeys()
    {
        Ped player = Game.Player.Character;

        // Revisamos cada 1s, pero OJO: antes esto interrumpia la tarea del
        // mono SIEMPRE, este haciendo lo que este haciendo - por eso se
        // veia en bucle/reseteandose todo el tiempo (le cortabamos la
        // persecucion en curso para volver a mandarle la misma orden).
        // Ahora solo tocamos al mono si esta huyendo de verdad; si ya te
        // esta persiguiendo, lo dejamos tranquilo.
        bool checkNow = (DateTime.Now - _lastMonkeyReassert).TotalSeconds >= 1;
        if (checkNow) _lastMonkeyReassert = DateTime.Now;

        for (int i = _activeKillerMonkeys.Count - 1; i >= 0; i--)
        {
            Ped monkey = _activeKillerMonkeys[i];

            if (monkey == null || !monkey.Exists() || !monkey.IsAlive)
            {
                _activeKillerMonkeys.RemoveAt(i);
                continue;
            }

            if (checkNow)
            {
                bool isFleeing = Function.Call<bool>(Hash.IS_PED_FLEEING, monkey.Handle);

                if (isFleeing)
                {
                    // Solo interrumpimos y reasignamos cuando hace falta:
                    // si no esta huyendo, lo dejamos hacer lo que este
                    // haciendo (perseguir, acercarse, etc) sin tocarlo.
                    Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, monkey.Handle);
                    Function.Call(Hash.TASK_COMBAT_PED, monkey.Handle, player, 0, 16);
                    Function.Call(Hash.TASK_GO_TO_ENTITY, monkey.Handle, player.Handle, -1, 1.0f, 3.0f, 1073741824f, 0);
                }
            }

            if (monkey.Position.DistanceTo(player.Position) < 1.5f)
            {
                player.Health = Math.Max(0, player.Health - (int)(40f * Game.LastFrameTime));
            }
        }
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
        // lejos), reponemos el marcador de guia para el proximo intento,
        // y limpiamos todo el caos activo para arrancar de cero.
        if (phaseChanged && (phase == "failed" || phase == "victory"))
        {
            Function.Call(Hash.SET_NEW_WAYPOINT, ChiliadSummit.X, ChiliadSummit.Y);
            CleanupAllChaos();
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
