namespace ConnectivityProbe.Monitor;

/// <summary>Veri dosyalarının güvenli yazımı: önce geçici dosyaya yazılır, sonra yerine taşınır.</summary>
internal static class AtomicFile
{
    /// <summary>
    /// Geçici dosyayı hedefin yerine taşır. Windows'ta antivirüs / indeksleyici yeni yazılmış dosyayı bir anlığına
    /// kilitleyebilir ("erişim reddedildi"); bu durumda kısa aralıklarla birkaç kez yeniden denenir.
    /// </summary>
    public static void Replace(string tmp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }
}
