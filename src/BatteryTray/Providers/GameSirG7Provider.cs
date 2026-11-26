using System.Runtime.InteropServices;
using BatteryTray.Core;
using BatteryTray.Interop;

namespace BatteryTray.Providers;

/// <summary>
/// Fork pedrocirinoo: estado do leitor GameSir G7 Pro, visivel mesmo sem o leitor compilado
/// (o XInputProvider pergunta aqui para nao listar o controle duas vezes).
/// </summary>
public static class GameSirG7
{
    public const ushort Vid = 0x3537;
    public const ushort PidPc = 0x100A;   // receptor no modo Xbox 360: sem bateria
    public const ushort PidXbox = 0x109C; // receptor no modo Xbox One (GIP): com bateria

    /// <summary>O leitor esta ativo e cuida do G7 Pro.</summary>
    public static volatile bool Active;

    /// <summary>O XInput deve pular este slot: e' o G7 Pro, lido pelo leitor GameSir.</summary>
    public static bool Owns(ushort vid, ushort pid) =>
        Active && (vid == Vid || (vid == 0x045E && pid == 0x02FF));
}

#if GAMESIR_D4X
/// <summary>
/// GameSir G7 Pro pelo receptor 3537:100A/109C (descoberto em 2026-11-26 com USBPcap):
///  1. No modo PC (100A) o receptor nao informa bateria. O GameSir Nexus troca o modo
///     mandando a senha "gamesirapp" escondida em comandos de vibracao (XInputSetState,
///     pares de letras no byte alto de cada motor, cada par seguido de vibracao zero).
///     O receptor reinicia como 109C (GIP, protocolo do Xbox One) em ~1 s.
///  2. No modo 109C, a mensagem GIP 0x0F com F2 (depois F2 00) liga por ~2 s o fluxo da
///     mensagem 0x10 (60 bytes, comeca com E0): [29] = bateria %, [28] = carregando.
///  3. O Windows so entrega a 0x10 pela DLL D4XGaming.Devices do Nexus (evento MessageB);
///     o envio vai pela API do Windows (GipGameControllerProvider.SendMessage).
///  4. Enquanto o processo mantem o registro, o receptor fica em 109C; ao fechar o Joule
///     o Windows desliga a sessao GIP e o receptor volta sozinho para 100A.
/// A DLL e' carregada da pasta do Nexus instalado; nada da GameSir vai no repositorio.
/// </summary>
public sealed class GameSirG7Provider : IStandaloneProvider
{
    private const string NexusFamily = "GuangzhouChickenRunNetwor.16460D2A87234_0zmrycy25r4k8";
    private static readonly Guid GameSirInterface = new("822bf47e-89e8-4c04-8182-0187a7588785");
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SwitchWait = TimeSpan.FromSeconds(8);

    private static readonly object Gate = new();
    private static bool _started;
    private static string? _startError;
    private static D4XGaming.Devices.D4XDevice? _device;
    private static Windows.Gaming.Input.Custom.GipGameControllerProvider? _gip;
    private static readonly Factory _factory = new();
    private static readonly AutoResetEvent _frame = new(false);
    private static (int Percent, bool Charging, DateTime Utc)? _last;
    private static bool _heartbeatSent;
    private static DateTime _lastSwitchUtc = DateTime.MinValue;

    public string Name => "GameSir G7 Pro";

    /// <summary>Abre o canal ja na criacao: o XInput (lido antes) precisa saber que o G7 Pro e' deste leitor.</summary>
    public GameSirG7Provider() => Start();

    // Tambem o lado XInput do G7 Pro no modo Xbox One (045E:02FF): nao vira "Controle Xbox" em "outros".
    public bool Handles(ushort vid, ushort pid) =>
        (vid == GameSirG7.Vid && (pid == GameSirG7.PidPc || pid == GameSirG7.PidXbox)) || GameSirG7.Owns(vid, pid);

    public IEnumerable<DeviceEntry> Discover(IReadOnlyList<HidCollection> collections)
    {
        bool pcMode = collections.Any(c => c.Vid == GameSirG7.Vid && c.Pid == GameSirG7.PidPc);
        if (!Start())
        {
            if (pcMode) Log.Write($"GameSir G7 Pro: leitor indisponivel ({_startError}).");
            return Array.Empty<DeviceEntry>();
        }

        if (_device is null && pcMode)
        {
            if (DateTime.UtcNow - _lastSwitchUtc > TimeSpan.FromMinutes(1))
            {
                _lastSwitchUtc = DateTime.UtcNow;
                bool sent = SwitchToXboxMode();
                Log.Write($"GameSir G7 Pro: troca do receptor para o modo com bateria {(sent ? "enviada" : "sem slot XInput do 100A")}.");
                var until = DateTime.UtcNow + SwitchWait;
                while (_device is null && DateTime.UtcNow < until) Thread.Sleep(200);
            }
        }

        if (_device is null && !pcMode)
        {
            // Receptor ja no modo com bateria: o Windows entrega o aparelho logo depois do registro.
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (_device is null && DateTime.UtcNow < until) Thread.Sleep(100);
            if (_device is null) return Array.Empty<DeviceEntry>();
        }

        return new[]
        {
            new DeviceEntry
            {
                Key = "gamesir:g7pro",
                DisplayName = "GameSir G7 Pro",
                HardwareId = Known.Id(GameSirG7.Vid, _device is null ? GameSirG7.PidPc : GameSirG7.PidXbox),
                ProviderName = Name,
                Kind = DeviceKind.Gamepad,
                Origin = "Dongle",
                IsConnected = true,
                Reading = _device is null
                    ? BatteryReading.NoData(L.T("trocando o receptor para o modo com bateria; atualize em alguns segundos",
                        "switching the receiver to the battery mode; refresh in a few seconds"))
                    : Read(),
            },
        };
    }

    private static BatteryReading Read()
    {
        var gip = _gip;
        if (gip is null)
            return BatteryReading.NoData(L.T("canal do controle ainda nao abriu", "controller channel not open yet"));

        // O sinal de 1 s mantem o fluxo: valor de agora dispensa pergunta nova.
        if (_last is { } recente && DateTime.UtcNow - recente.Utc < TimeSpan.FromSeconds(3))
            return BatteryReading.Percentage(recente.Percent, chargeText: recente.Charging ? L.T("carregando", "charging") : null);

        _frame.Reset();
        var asked = DateTime.UtcNow;
        try
        {
            // Igual ao Nexus: F2 e, logo em seguida, 09 (sem o 09 o controle manda so o fluxo de movimento).
            _heartbeatSent = false;
            OnPump(Heartbeat);
        }
        catch (Exception ex) { Log.Error("GameSirG7.Send", ex); }

        // Logo depois da troca de modo o controle ainda esta reconectando ao receptor: espera mais.
        var deadline = asked + (DateTime.UtcNow - _addedUtc < TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(8) : ReplyTimeout);
        while (DateTime.UtcNow < deadline)
        {
            _frame.WaitOne(TimeSpan.FromMilliseconds(200));
            if (_last is { } v && v.Utc >= asked)
                return BatteryReading.Percentage(v.Percent, chargeText: v.Charging ? L.T("carregando", "charging") : null);
        }
        Log.Write($"GameSir G7 Pro: sem quadro de bateria (mensagens recebidas={_msgs}, com bateria={_msgsE0}).");
        return BatteryReading.NoData(L.T("controle sem resposta. Mexa nele e atualize", "controller did not respond. Move it and refresh"));
    }

    private static int _msgs, _msgsE0;
    private static DateTime _lastMsgUtc = DateTime.MinValue;
    private static DateTime _addedUtc = DateTime.MinValue;

    private static void OnMessageB(D4XGaming.Devices.D4XDevice device, byte[] payload)
    {
        Interlocked.Increment(ref _msgs);
        _lastMsgUtc = DateTime.UtcNow;
        if (payload.Length != 60 || payload[0] != 0xE0) return;
        Interlocked.Increment(ref _msgsE0);
        int percent = payload[29];
        if (percent is <= 0 or > 100) return;
        _last = (percent, payload[28] == 1, DateTime.UtcNow);
        _frame.Set();
    }

    // ---- Inicio: carrega a DLL do Nexus e abre o canal GIP ----

    private static bool Start()
    {
        lock (Gate)
        {
            if (_started) return _startError is null;
            _started = true;
            try
            {
                EnsurePump();
                OnPump(StartOnPump);
            }
            catch (Exception ex)
            {
                _startError = ex.Message;
                Log.Error("GameSirG7.Start", ex);
            }
            return _startError is null;
        }
    }

    // A DLL do Nexus e a API de controles do Windows so entregam mensagens a uma thread STA
    // com fila de mensagens (medido em 2026-11-26: no Joule, sem isso, 0 mensagens).
    private static System.Windows.Forms.Control? _pump;

    private static void EnsurePump()
    {
        if (_pump is not null) return;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            var c = new System.Windows.Forms.Control();
            c.CreateControl();
            _ = c.Handle;
            _pump = c;
            ready.Set();
            System.Windows.Forms.Application.Run();
        }) { IsBackground = true, Name = "GameSirG7" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
    }

    private static void OnPump(Action a) => _pump!.Invoke(a);

    private static void StartOnPump()
    {
        LoadNexusDll();
        D4XGaming.Devices.D4XDevice.D4XDeviceAdded += (_, d) =>
        {
            if (d.HardwareVendorId != GameSirG7.Vid) return;
            d.MessageBReceived += OnMessageB;
            _addedUtc = DateTime.UtcNow;
            _device = d;
            _heartbeatSent = false;
            Log.Write($"GameSir G7 Pro: canal aberto ({d.HardwareVendorId:X4}:{d.HardwareProductId:X4}).");
        };
        D4XGaming.Devices.D4XDevice.D4XDeviceRemoved += (_, d) =>
        {
            if (d.HardwareVendorId != GameSirG7.Vid) return;
            _device = null;
            _gip = null;
        };
        Windows.Gaming.Input.Custom.GameControllerFactoryManager.RegisterCustomFactoryForGipInterface(_factory, GameSirInterface);
        GameSirG7.Active = true;

        // Sem sinal por ~4 s o receptor volta sozinho ao modo PC (100A) e o controle desconecta.
        // O Nexus manda a cada 0,4 s; aqui, a cada 1 s, na mesma thread STA.
        var batida = new System.Windows.Forms.Timer { Interval = 1000 };
        batida.Tick += (_, _) => Heartbeat();
        batida.Start();
        _batida = batida;
    }

    private static System.Windows.Forms.Timer? _batida;

    private static void Heartbeat()
    {
        var gip = _gip;
        if (gip is null || _device is null) return;
        try
        {
            // Sem mensagem ha 3 s (controle ainda reconectando ao receptor, ou fluxo parado):
            // repete o sinal que LIGA o fluxo. Com o fluxo vivo, so o sinal que o mantem.
            if (DateTime.UtcNow - _lastMsgUtc > TimeSpan.FromSeconds(3)) _heartbeatSent = false;
            if (!_heartbeatSent)
            {
                gip.SendMessage(Windows.Gaming.Input.Custom.GipMessageClass.Command, 0x0F, new byte[] { 0xF2 });
                Thread.Sleep(5);
                gip.SendMessage(Windows.Gaming.Input.Custom.GipMessageClass.Command, 0x0F, new byte[] { 0x09 });
                _heartbeatSent = true;
            }
            else gip.SendMessage(Windows.Gaming.Input.Custom.GipMessageClass.Command, 0x0F, new byte[] { 0xF2, 0x00 });
        }
        catch (Exception ex) { Log.Error("GameSirG7.Heartbeat", ex); }
    }

    private static void LoadNexusDll()
    {
        var pm = new Windows.Management.Deployment.PackageManager();
        var nexus = pm.FindPackagesForUser(string.Empty, NexusFamily).FirstOrDefault()
            ?? throw new InvalidOperationException("GameSir Nexus nao instalado");
        string? vc = nexus.Dependencies
            .Where(d => d.Id.Name == "Microsoft.VCLibs.140.00")
            .Select(d => d.InstalledLocation.Path)
            .FirstOrDefault(p => File.Exists(Path.Combine(p, "vcruntime140_1_app.dll")))
            ?? throw new InvalidOperationException("VCLibs x64 do Nexus nao encontrado");
        foreach (var dll in new[] { "vcruntime140_app.dll", "vcruntime140_1_app.dll", "msvcp140_app.dll" })
        {
            string path = Path.Combine(vc, dll);
            if (!NativeLibrary.TryLoad(path, out _))
                throw new DllNotFoundException($"{dll} nao carregou de {vc} (erro {Marshal.GetLastPInvokeError()})");
        }
        // Carregada pelo caminho: a ativacao do CsWinRT procura "D4XGaming.Devices.dll" pelo nome e acha esta.
        // A pasta WindowsApps deixa ler mas nao executar a DLL de outro pacote (erro 5):
        // copia local na pasta de dados do Joule, refeita se o Nexus atualizar a DLL.
        string origem = Path.Combine(nexus.InstalledLocation.Path, "D4XGaming.Devices.dll");
        string pasta = Path.Combine(Path.GetDirectoryName(AppPaths.SettingsFile)!, "gamesir");
        string d4x = Path.Combine(pasta, "D4XGaming.Devices.dll");
        Directory.CreateDirectory(pasta);
        var o = new FileInfo(origem);
        var c = new FileInfo(d4x);
        if (!c.Exists || c.Length != o.Length || c.LastWriteTimeUtc != o.LastWriteTimeUtc)
        {
            File.Copy(origem, d4x, overwrite: true);
            File.SetLastWriteTimeUtc(d4x, o.LastWriteTimeUtc);
        }
        if (!NativeLibrary.TryLoad(d4x, out _))
            throw new DllNotFoundException("D4XGaming.Devices.dll nao carregou da copia local");
    }

    // ---- Troca de modo: "gamesirapp" nos motores de vibracao do slot do receptor 100A ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Vibration { public ushort Left; public ushort Right; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    private static extern uint XInputSetState(uint userIndex, ref Vibration vibration);

    private static bool SwitchToXboxMode()
    {
        if (!XInputNative.IsAvailable()) return false;
        bool any = false;
        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputNative.GetState(slot, out _) != XInputNative.ERROR_SUCCESS) continue;
            if (XInputNative.GetCapabilitiesEx(slot) is not { Vid: GameSirG7.Vid, Pid: GameSirG7.PidPc }) continue;
            const string senha = "gamesirapp";
            for (int i = 0; i < senha.Length; i += 2)
            {
                var v = new Vibration { Left = (ushort)(senha[i] << 8), Right = (ushort)(senha[i + 1] << 8) };
                XInputSetState(slot, ref v);
                Thread.Sleep(15);
                var zero = new Vibration();
                XInputSetState(slot, ref zero);
                Thread.Sleep(15);
            }
            any = true;
        }
        return any;
    }

    private sealed class Factory : Windows.Gaming.Input.Custom.ICustomGameControllerFactory
    {
        // O objeto devolvido ao Windows precisa ficar vivo: a limpeza de memoria do fork (GC forcado)
        // o coletava e o Windows passava a ignorar os envios do provider (medido com USBPcap).
        private readonly List<object> _vivos = new();

        public object CreateGameController(Windows.Gaming.Input.Custom.IGameControllerProvider provider)
        {
            Log.Write($"GameSir G7 Pro: provider {provider.HardwareVendorId:X4}:{provider.HardwareProductId:X4} tipo={provider.GetType().Name} conectado={provider.IsConnected}.");
            if (provider is Windows.Gaming.Input.Custom.GipGameControllerProvider gip && provider.HardwareVendorId == GameSirG7.Vid)
                _gip = gip;
            var controle = new object();
            lock (_vivos) _vivos.Add(controle);
            return controle;
        }

        public void OnGameControllerAdded(Windows.Gaming.Input.IGameController value) { }
        public void OnGameControllerRemoved(Windows.Gaming.Input.IGameController value) { }
    }
}
#endif
