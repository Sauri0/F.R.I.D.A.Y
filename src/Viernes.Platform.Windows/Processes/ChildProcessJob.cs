using System.Runtime.InteropServices;

namespace Viernes.Platform.Windows.Processes;

/// <summary>
/// Hace que los procesos que Viernes levanta se mueran con Viernes, se muera como se muera.
/// </summary>
/// <remarks>
/// <b>Esto existe por setenta y seis procesos huérfanos.</b> Los servidores MCP son ejecutables
/// aparte —el de Spotify es un <c>node</c>— y viven mientras dure la sesión. Cerrar la sesión como
/// corresponde los mata: está medido, se levantó uno y al llamar a <c>DisposeAsync</c> desapareció.
/// El problema es todo lo demás. Un cierre forzado desde el administrador de tareas, un cuelgue,
/// apagar la máquina con la aplicación abierta: en ninguno de esos casos corre el cierre, y el hijo
/// queda dando vueltas para siempre. Uno por arranque. En la máquina del usuario se contaron setenta
/// y seis, algunos de veintidós horas.
/// <para>
/// El arreglo no es acordarse de cerrar mejor —de un cierre forzado no hay forma de acordarse— sino
/// atar la vida del hijo a la del padre a nivel del sistema. Windows tiene exactamente eso: un
/// <em>job</em> con <c>KILL_ON_JOB_CLOSE</c>. Todo proceso que esté adentro muere cuando se cierra el
/// último identificador del job, y el del propio proceso lo cierra el sistema operativo al terminar,
/// pase lo que pase. No hace falta que Viernes se entere de que se está muriendo.
/// </para>
/// <para>
/// Por eso el identificador se guarda y <b>nunca</b> se cierra a mano: cerrarlo antes mataría a los
/// hijos en pleno uso. Lo cierra el sistema, y ése es el momento correcto.
/// </para>
/// <para>
/// <b>Por descendencia Y por nombre de ejecutable, y las dos mitades hacen falta.</b> Sólo por
/// nombre sería adivinar: en esa máquina hay decenas de <c>node</c> que no son de acá. Sólo por
/// descendencia sería un desastre silencioso, porque cuando el usuario le pide que abra Spotify esa
/// aplicación también nace descendiente de Viernes, y cerrar el asistente le cerraría de golpe lo
/// que le pidió que abriera.
/// </para>
/// <para>
/// Se ve en el contador: sin el filtro por nombre se ataban 3 procesos, con el filtro 1.
/// </para>
/// </remarks>
public static class ChildProcessJob
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x2000;

    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    private static readonly Lock Gate = new();
    private static readonly HashSet<uint> Adopted = [];

    private static nint _job;
    private static bool _unavailable;

    /// <summary>Cuántos procesos hijos se ataron a la vida de éste.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Adopted.Count;
            }
        }
    }

    /// <summary>
    /// Ata a la vida de este proceso los descendientes que corran alguno de esos ejecutables.
    /// </summary>
    /// <remarks>
    /// <b>Por nombre de ejecutable Y por descendencia, y las dos mitades hacen falta.</b> Atar toda
    /// la descendencia sin mirar qué es sería un desastre silencioso: cuando el usuario le pide que
    /// abra Spotify, esa aplicación también nace descendiente de Viernes, y cerrar el asistente le
    /// cerraría de golpe lo que le había pedido que abriera. Y sólo por nombre tampoco: mataría
    /// procesos ajenos que casualmente se llamen igual.
    /// <para>
    /// Se puede llamar cuantas veces haga falta: los que ya están adentro se saltean. Conviene
    /// llamarla después de cada conexión y de cada reconexión de un servidor, porque una reconexión
    /// levanta un proceso nuevo — y un proceso nuevo sin adoptar es un huérfano nuevo.
    /// </para>
    /// </remarks>
    /// <param name="images">
    /// Los ejecutables de los servidores, como los nombra la configuración. Se compara contra el
    /// nombre del archivo, sin ruta y sin importar mayúsculas: <c>node</c> alcanza para
    /// <c>node.exe</c>, y una ruta entera también sirve porque se le saca la carpeta.
    /// </param>
    /// <returns>Cuántos se adoptaron en esta llamada.</returns>
    public static int Adopt(IEnumerable<string> images)
    {
        ArgumentNullException.ThrowIfNull(images);

        var buscados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images)
        {
            if (string.IsNullOrWhiteSpace(image))
            {
                continue;
            }

            var nombre = Path.GetFileName(image.Trim());
            buscados.Add(nombre);
            if (!nombre.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                buscados.Add(nombre + ".exe");
            }

            buscados.UnionWith(LoQueRealmenteCorre(nombre));
        }

        if (buscados.Count == 0)
        {
            return 0;
        }

        lock (Gate)
        {
            if (_unavailable || !EnsureJob())
            {
                return 0;
            }

            var propios = 0;
            foreach (var (child, imagen) in Descendants((uint)Environment.ProcessId))
            {
                if (!buscados.Contains(imagen))
                {
                    continue;
                }

                // Se anota antes de intentar y no después: si el sistema no deja adoptarlo, queda
                // anotado igual. Reintentar en cada reconexión un proceso que ya se negó una vez es
                // gastar llamadas para volver a fallar.
                if (!Adopted.Add(child))
                {
                    continue;
                }

                if (Assign(child))
                {
                    propios++;
                }
            }

            return propios;
        }
    }

    /// <summary>
    /// Qué proceso queda vivo de verdad cuando el comando configurado es un guión.
    /// </summary>
    /// <remarks>
    /// <b>Sin esto la protección quedaba en cero, y en silencio, para la forma más habitual de
    /// declarar un servidor MCP en Windows.</b> <c>npx</c>, <c>npm</c> y compañía no son ejecutables:
    /// son <c>.cmd</c> que arrancan otra cosa y se van. Buscando un proceso llamado «npx» no se
    /// encuentra ninguno, así que no se adoptaba nada — ni un error, ni un renglón, ni nada.
    /// <para>
    /// Lo que queda vivo es el intérprete. Se agregan los que corresponden al lanzador, y también el
    /// <c>cmd.exe</c> que Windows usa para correr un <c>.cmd</c>.
    /// </para>
    /// <para>
    /// <b>Es una lista y las listas envejecen.</b> Un lanzador que no esté acá vuelve a no adoptar
    /// nada — pero ya no en silencio: quien llama informa cuántos ató, y cero con servidores
    /// levantados es visible en la bitácora. Lo correcto de verdad sería mirar la línea de comando de
    /// cada descendiente y quedarse con los que mencionan el guión configurado; eso pide leer el
    /// bloque de entorno de otro proceso y quedó anotado como lo que falta.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> LoQueRealmenteCorre(string nombre)
    {
        var pelado = Path.GetFileNameWithoutExtension(nombre);

        if (nombre.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            nombre.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            yield return "cmd.exe";
        }

        switch (pelado.ToLowerInvariant())
        {
            case "npx":
            case "npm":
            case "yarn":
            case "pnpm":
            case "bunx":
                yield return "node.exe";
                yield return "cmd.exe";
                break;

            case "uv":
            case "uvx":
            case "pipx":
                yield return "python.exe";
                yield return "pythonw.exe";
                yield return "cmd.exe";
                break;

            case "deno":
                yield return "deno.exe";
                break;
        }
    }

    /// <summary>
    /// Arma el job la primera vez. Si el sistema no deja, se apaga solo y no se vuelve a intentar.
    /// </summary>
    /// <remarks>
    /// No poder armarlo no puede impedir que el asistente arranque: se pierde la garantía de que los
    /// hijos se mueran solos, que es exactamente donde estábamos antes de esto.
    /// </remarks>
    private static bool EnsureJob()
    {
        if (_job != nint.Zero)
        {
            return true;
        }

        var job = CreateJobObject(nint.Zero, null);
        if (job == nint.Zero)
        {
            _unavailable = true;
            return false;
        }

        var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        info.BasicLimitInformation.LimitFlags = LimitKillOnJobClose;

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                // Un job sin «matar al cerrar» no sirve para nada: no atarlo es más honesto que
                // dejarlo puesto y creer que protege.
                CloseHandle(job);
                _unavailable = true;
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        _job = job;
        return true;
    }

    private static bool Assign(uint processId)
    {
        var handle = OpenProcess(ProcessSetQuota | ProcessTerminate, bInheritHandle: false, processId);
        if (handle == nint.Zero)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(_job, handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Todos los procesos vivos que descienden de éste, no sólo los hijos directos.
    /// </summary>
    /// <remarks>
    /// La foto del sistema puede traer un proceso que ya murió y cuyo número lo reusó otro: entre
    /// sacar la foto y abrir el proceso pasa tiempo. No es un riesgo real acá porque lo único que se
    /// hace con él es meterlo en el job, y meter en el job a un desconocido de paso no lo mata — lo
    /// mataría recién cuando muera Viernes, que es cuando ya no importa. Matarlo directo, en cambio,
    /// sí sería grave, y por eso acá no se mata a nadie.
    /// </remarks>
    /// <remarks>
    /// <b>Toda la descendencia y no los hijos, y eso está medido.</b> El SDK de MCP no lanza el
    /// servidor directo: en Windows lo envuelve, así que el <c>node</c> de Spotify no es hijo de
    /// Viernes sino nieto. Adoptando sólo a los hijos, lo que entraba al job era el envoltorio, y al
    /// morir Viernes el job mataba al envoltorio y dejaba al nieto suelto — exactamente el huérfano
    /// que esto viene a evitar. Probado: con adopción de hijos, matar Viernes a la fuerza dejaba 1
    /// huérfano; con adopción de la descendencia, 0.
    /// <para>
    /// La herencia del job no alcanza para esto. Un proceso creado por otro que ya está adentro nace
    /// adentro, pero acá se adopta <em>después</em> de que el servidor ya arrancó: el nieto ya existía
    /// cuando el envoltorio entró. Por eso se asigna uno por uno en vez de confiar en que se hereden.
    /// </para>
    /// </remarks>
    private static IEnumerable<(uint Id, string Image)> Descendants(uint rootId)
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == nint.Zero || snapshot == new nint(-1))
        {
            yield break;
        }

        try
        {
            var entry = default(PROCESSENTRY32);
            entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();

            if (!Process32First(snapshot, ref entry))
            {
                yield break;
            }

            var porPadre = new Dictionary<uint, List<uint>>();
            var imagenes = new Dictionary<uint, string>();
            do
            {
                if (entry.th32ProcessID == 0 || entry.th32ProcessID == entry.th32ParentProcessID)
                {
                    continue;
                }

                if (!porPadre.TryGetValue(entry.th32ParentProcessID, out var hijos))
                {
                    hijos = [];
                    porPadre[entry.th32ParentProcessID] = hijos;
                }

                hijos.Add(entry.th32ProcessID);
                imagenes[entry.th32ProcessID] = entry.szExeFile ?? string.Empty;
            }
            while (Process32Next(snapshot, ref entry));

            // Por niveles, y con los ya vistos anotados: el número de un proceso muerto lo puede
            // reusar otro, y un padre que apunta a un descendiente suyo arma un ciclo que dejaría
            // esto girando para siempre.
            var vistos = new HashSet<uint> { rootId };
            var pendientes = new Queue<uint>();
            pendientes.Enqueue(rootId);

            while (pendientes.Count > 0)
            {
                if (!porPadre.TryGetValue(pendientes.Dequeue(), out var hijos))
                {
                    continue;
                }

                foreach (var hijo in hijos)
                {
                    if (!vistos.Add(hijo))
                    {
                        continue;
                    }

                    pendientes.Enqueue(hijo);
                    yield return (hijo, imagenes.GetValueOrDefault(hijo, string.Empty));
                }
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint job,
        uint infoClass,
        nint info,
        uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
