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

    /// <summary>A % ou o "carregando" mudou: o ReadingService repinta so esta linha, sem tocar em nada.</summary>
    public static event Action? ValueChanged;

    /// <summary>Linha do G7 Pro com o ultimo valor ouvido (sem perguntar nada); null = sem canal.</summary>
    public static Func<DeviceEntry?>? LiveEntry;

    internal static void RaiseChanged() => ValueChanged?.Invoke();
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
    private static DateTime _lastSwitchUtc = DateTime.MinValue;
    private static int _switchFails;
    private static int _inits;
    private static volatile bool _frameThisConn;

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
            // Cada troca desconecta o controle uns 4 s: depois de uma troca que nao pegou,
            // espera 2, 4, 8... ate 30 min antes da proxima (nao atrapalhar no meio do jogo).
            var espera = TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, _switchFails)));
            if (DateTime.UtcNow - _lastSwitchUtc > espera)
            {
                _lastSwitchUtc = DateTime.UtcNow;
                bool sent = SwitchToXboxMode();
                Log.Write($"GameSir G7 Pro: troca do receptor para o modo com bateria {(sent ? "enviada" : "sem slot XInput do 100A")}.");
                var until = DateTime.UtcNow + SwitchWait;
                while (_device is null && DateTime.UtcNow < until) Thread.Sleep(200);
                if (_device is null) _switchFails++;
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
            Entry(_device is null
                ? BatteryReading.NoData(L.T("trocando o receptor para o modo com bateria; o valor aparece sozinho em seguida",
                    "switching the receiver to the battery mode; the value shows up on its own"))
                : Read()),
        };
    }

    private static DeviceEntry Entry(BatteryReading reading) => new()
    {
        Key = "gamesir:g7pro",
        DisplayName = "GameSir G7 Pro",
        HardwareId = Known.Id(GameSirG7.Vid, _device is null ? GameSirG7.PidPc : GameSirG7.PidXbox),
        ProviderName = "GameSir G7 Pro",
        Kind = DeviceKind.Gamepad,
        Origin = "Dongle",
        IsConnected = true,
        IsLive = true,
        Reading = reading,
    };

    private static BatteryReading FromLast(int percent, bool charging, DateTime utc) =>
        BatteryReading.Percentage(percent, observedUtc: utc, chargeText: charging ? L.T("carregando", "charging") : null);

    /// <summary>Linha ao vivo, so com o valor ja ouvido: nenhuma pergunta, nenhuma espera.</summary>
    private static DeviceEntry? Live() =>
        _device is null || _last is not { } v ? null : Entry(FromLast(v.Percent, v.Charging, v.Utc));

    private static BatteryReading Read()
    {
        var gip = _gip;
        if (gip is null)
            return BatteryReading.NoData(L.T("canal do controle ainda nao abriu", "controller channel not open yet"));

        // A amostra de 30 s mantem o valor fresco: a leitura so devolve o ultimo valor ouvido.
        // Nunca reenvia o sinal inicial aqui: o 09 faz a luz do Xbox piscar (relato do Pedro, 2026-11-26).
        if (_last is { } recente)
            return FromLast(recente.Percent, recente.Charging, recente.Utc);

        _frame.Reset();
        var asked = DateTime.UtcNow;
        try { OnPump(() => Listen(true)); }
        catch (Exception ex) { Log.Error("GameSirG7.Listen", ex); }

        // Logo depois da troca de modo o controle ainda esta reconectando ao receptor: espera mais.
        var deadline = asked + (DateTime.UtcNow - _addedUtc < TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(8) : ReplyTimeout);
        while (DateTime.UtcNow < deadline)
        {
            _frame.WaitOne(TimeSpan.FromMilliseconds(200));
            if (_last is { } v && v.Utc >= asked)
                return FromLast(v.Percent, v.Charging, v.Utc);
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
        bool charging = payload[28] == 1;
        bool mudou = _last is not { } antes || antes.Percent != percent || antes.Charging != charging;
        _last = (percent, charging, DateTime.UtcNow);
        _gotFrame = true;
        _frameThisConn = true;
        _frame.Set();
        if (mudou) GameSirG7.RaiseChanged();
    }

    // ---- Escuta em amostras: o fluxo vem a ~60/s, mas a bateria muda devagar ----
    // Ouvir sempre custava ~6% de um nucleo (medido em 2026-11-26). Ouve ate o 1o quadro
    // a cada 30 s e solta: a tela continua ao vivo (atualiza sozinha quando o valor muda).
    private static readonly D4XGaming.Devices.MessageBReceivedEvent Ouvinte = OnMessageB;
    private static readonly TimeSpan Amostra = TimeSpan.FromSeconds(30);
    private static bool _listening, _gotFrame;
    private static DateTime _listenStartUtc, _lastSampleUtc = DateTime.MinValue;

    /// <summary>So na thread do leitor.</summary>
    private static void Listen(bool on)
    {
        var d = _device;
        if (d is null) { _listening = false; return; }
        try
        {
            if (on && !_listening)
            {
                _gotFrame = false;
                _listenStartUtc = DateTime.UtcNow;
                _lastSampleUtc = DateTime.UtcNow;
                d.MessageBReceived += Ouvinte;
                _listening = true;
            }
            else if (!on && _listening)
            {
                d.MessageBReceived -= Ouvinte;
                _listening = false;
            }
        }
        catch (Exception ex) { Log.Error("GameSirG7.Listen", ex); }
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
            _addedUtc = DateTime.UtcNow;
            _switchFails = 0;
            _inits = 0;
            _frameThisConn = false;
            _device = d;
            _listening = false;
            Log.Write($"GameSir G7 Pro: canal aberto ({d.HardwareVendorId:X4}:{d.HardwareProductId:X4}).");
            _pump?.BeginInvoke(() => Listen(true));
        };
        D4XGaming.Devices.D4XDevice.D4XDeviceRemoved += (_, d) =>
        {
            if (d.HardwareVendorId != GameSirG7.Vid) return;
            _device = null;
            _gip = null;
            _listening = false;
        };
        GameSirG7.LiveEntry = Live;
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

        // Amostra: abre a escuta a cada 30 s; fecha no 1o quadro com bateria ou depois de 5 s.
        var agora = DateTime.UtcNow;
        if (!_listening && agora - _lastSampleUtc > Amostra) Listen(true);
        else if (_listening && (_gotFrame || agora - _listenStartUtc > TimeSpan.FromSeconds(5)) && _last is not null) Listen(false);

        try
        {
            // Sinal inicial (F2 + 09) so nas 3 primeiras batidas de cada conexao: cobre o controle
            // ainda reconectando, e o 09 faz a luz do Xbox piscar, entao nunca mais depois disso.
            // Fluxo sem valor: F2 sozinho (sem 09). Com valor: F2 00, que so mantem o modo.
            if (_inits < 3 && !_frameThisConn)
            {
                gip.SendMessage(Windows.Gaming.Input.Custom.GipMessageClass.Command, 0x0F, new byte[] { 0xF2 });
                Thread.Sleep(5);
                gip.SendMessage(Windows.Gaming.Input.Custom.GipMessageClass.Command, 0x0F, new byte[] { 0x09 });
                _inits++;
            }
            else if (_last is not { } v || agora - v.Utc > TimeSpan.FromSeconds(45))
                gip.SendMessage(Windows.Gaming.Input.Custom.GipMessageClass.Command, 0x0F, new byte[] { 0xF2 });
            else
                gip.SendMessage(Windows.Gaming.Input.Custom.GipMessageClass.Command, 0x0F, new byte[] { 0xF2, 0x00 });
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
