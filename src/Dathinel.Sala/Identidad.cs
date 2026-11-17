using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Dathinel.Sala;

/// <summary>
/// Clave privada del equipo (Ed25519 de iroh) en 64 hex. <see cref="CargarOCrear"/> la guarda cifrada con
/// DPAPI del usuario actual en Windows y en un archivo 0600 en Linux y macOS; no usa el llavero del sistema.
/// </summary>
public static partial class Identidad
{
    private const uint UiProhibida = 0x1; // CRYPTPROTECT_UI_FORBIDDEN
    private static readonly byte[] s_entropia = Encoding.UTF8.GetBytes("Dathinel.Sala.Identidad.v1");

    public static string Nueva() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Devuelve la clave de <paramref name="archivo"/>; si no existe, crea una y la guarda de forma atómica
    /// (temporal en la misma carpeta y File.Move sin sobrescribir). Si dos instancias la crean a la vez,
    /// todas devuelven la que quedó guardada. Un archivo dañado, de otro usuario de Windows o con permisos
    /// demasiado abiertos lanza <see cref="SalaException"/> con código «identidad»: no se pisa en silencio.
    /// </summary>
    public static string CargarOCrear(string archivo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivo);
        if (File.Exists(archivo)) return Leer(archivo);
        string clave = Nueva();
        return Guardar(archivo, clave);
    }

    private static string Leer(string archivo)
    {
        byte[]? claro = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                claro = Desproteger(LeerBytes(archivo), archivo);
            }
            else
            {
                RevisarPermisosUnix(archivo);
                try
                {
                    claro = Convert.FromHexString(Encoding.ASCII.GetString(LeerBytes(archivo)).Trim());
                }
                catch (FormatException ex)
                {
                    throw Dañado(archivo, ex);
                }
            }
            if (claro.Length != 32) throw Dañado(archivo);
            return Convert.ToHexStringLower(claro);
        }
        finally
        {
            if (claro is not null) CryptographicOperations.ZeroMemory(claro);
        }
    }

    /// <summary>Publica <paramref name="clave"/> sin sobrescribir; si otra instancia ganó, devuelve la suya.</summary>
    private static string Guardar(string archivo, string clave)
    {
        string completa = Path.GetFullPath(archivo);
        if (Path.GetDirectoryName(completa) is { Length: > 0 } dir) Directory.CreateDirectory(dir);

        byte[] datos;
        if (OperatingSystem.IsWindows())
        {
            byte[] claro = Convert.FromHexString(clave);
            try
            {
                datos = Proteger(claro);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(claro);
            }
        }
        else
        {
            datos = Encoding.ASCII.GetBytes(clave);
        }

        string tmp = completa + ".tmp-" + Guid.NewGuid().ToString("N");
        bool movido = false;
        try
        {
            FileStreamOptions opt = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) opt.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (FileStream fs = new(tmp, opt))
            {
                fs.Write(datos);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, completa, overwrite: false);
            movido = true;
            return clave;
        }
        catch (IOException) when (File.Exists(completa))
        {
            // Otra instancia publicó primero: su clave es la que vale; el temporal se borra en el finally.
            return Leer(completa);
        }
        finally
        {
            if (!movido)
            {
                try { File.Delete(tmp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    // Windows: DPAPI del usuario actual (sin paquetes); el archivo solo lo descifra esta misma cuenta.
    private static byte[] Proteger(byte[] claro) => Dpapi(claro, proteger: true);

    private static byte[] Desproteger(byte[] cifrado, string archivo)
    {
        try
        {
            return Dpapi(cifrado, proteger: false);
        }
        catch (SalaException ex)
        {
            throw new SalaException("identidad",
                $"La identidad de «{archivo}» no se pudo descifrar: está dañada o la cifró otro usuario de Windows. " +
                "Si no la necesitas, bórrala y se creará una nueva.", ex);
        }
    }

    private static byte[] Dpapi(byte[] entrada, bool proteger)
    {
        GCHandle pEntrada = GCHandle.Alloc(entrada, GCHandleType.Pinned);
        GCHandle pEntropia = GCHandle.Alloc(s_entropia, GCHandleType.Pinned);
        IntPtr bIn = Marshal.AllocHGlobal(IntPtr.Size * 2);
        IntPtr bEntropia = Marshal.AllocHGlobal(IntPtr.Size * 2);
        IntPtr bOut = Marshal.AllocHGlobal(IntPtr.Size * 2);
        try
        {
            Marshal.WriteInt32(bIn, entrada.Length);
            Marshal.WriteIntPtr(bIn, IntPtr.Size, pEntrada.AddrOfPinnedObject());
            Marshal.WriteInt32(bEntropia, s_entropia.Length);
            Marshal.WriteIntPtr(bEntropia, IntPtr.Size, pEntropia.AddrOfPinnedObject());
            Marshal.WriteIntPtr(bOut, IntPtr.Zero);
            Marshal.WriteIntPtr(bOut, IntPtr.Size, IntPtr.Zero);
            bool ok = proteger
                ? CryptProtectData(bIn, IntPtr.Zero, bEntropia, IntPtr.Zero, IntPtr.Zero, UiProhibida, bOut)
                : CryptUnprotectData(bIn, IntPtr.Zero, bEntropia, IntPtr.Zero, IntPtr.Zero, UiProhibida, bOut);
            if (!ok)
                throw new SalaException("identidad",
                    $"Windows no pudo {(proteger ? "cifrar" : "descifrar")} la identidad (error {Marshal.GetLastPInvokeError()}).");
            int cb = Marshal.ReadInt32(bOut);
            IntPtr pb = Marshal.ReadIntPtr(bOut, IntPtr.Size);
            try
            {
                byte[] salida = new byte[cb];
                Marshal.Copy(pb, salida, 0, cb);
                return salida;
            }
            finally
            {
                Marshal.Copy(new byte[cb], 0, pb, cb); // puede llevar la clave en claro
                LocalFree(pb);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(bIn);
            Marshal.FreeHGlobal(bEntropia);
            Marshal.FreeHGlobal(bOut);
            pEntrada.Free();
            pEntropia.Free();
        }
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(IntPtr pDataIn, IntPtr pwszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, IntPtr pDataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(IntPtr pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, IntPtr pDataOut);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr LocalFree(IntPtr hMem);

    // Linux y macOS: 64 hex en un archivo que nació 0600; si el grupo u otros pueden tocarlo, se rechaza.
    private static void RevisarPermisosUnix(string archivo)
    {
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode prohibidos = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(archivo) & prohibidos) != 0)
            throw new SalaException("identidad",
                $"La identidad de «{archivo}» tiene permisos para el grupo u otros: déjala en 0600 (chmod 600) o bórrala.");
    }

    private static byte[] LeerBytes(string archivo)
    {
        try
        {
            return File.ReadAllBytes(archivo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new SalaException("identidad", $"No se pudo leer la identidad de «{archivo}»: {ex.Message}", ex);
        }
    }

    private static SalaException Dañado(string archivo, Exception? interna = null) => new("identidad",
        $"La identidad de «{archivo}» está dañada. Si no la necesitas, bórrala y se creará una nueva.", interna);
}
