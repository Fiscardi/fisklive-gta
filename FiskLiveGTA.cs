using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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

            default:
                GTA.UI.Notification.PostTicker("~y~FiskLive:~w~ accion desconocida '" + action + "'", false);
                break;
        }
    }

    // ---------- Acciones ----------

    private void SpawnVehicle(string modelName)
    {
        Model model = new Model(modelName);
        model.Request(1000);
        if (!model.IsLoaded)
        {
            GTA.UI.Notification.PostTicker("~r~No se pudo cargar el modelo:~w~ " + modelName, false);
            return;
        }

        Vector3 pos = Game.Player.Character.Position + Game.Player.Character.ForwardVector * 6f;
        Vehicle veh = World.CreateVehicle(model, pos);
        if (veh != null)
        {
            veh.PlaceOnGround();
            GTA.UI.Notification.PostTicker("~g~Vehiculo spawneado:~w~ " + modelName, false);
        }
        model.MarkAsNoLongerNeeded();
    }

    private void GiveWeapon(string weaponName)
    {
        try
        {
            WeaponHash hash = (WeaponHash)Enum.Parse(typeof(WeaponHash), weaponName, true);
            Game.Player.Character.Weapons.Give(hash, 250, true, true);
            GTA.UI.Notification.PostTicker("~g~Arma entregada:~w~ " + weaponName, false);
        }
        catch
        {
            GTA.UI.Notification.PostTicker("~r~Arma no reconocida:~w~ " + weaponName, false);
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
