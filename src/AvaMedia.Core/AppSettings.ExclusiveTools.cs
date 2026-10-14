namespace AvaMedia.Core;

public static class ExclusiveToolAccess
{
    private const string KeyDigest = "7a2ec40ff8a1247c532309355f798a779e00acff579c63eec3636ffb2902c1ac";
    public static bool Accepts(string key) => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)), Convert.FromHexString(KeyDigest));
}

public sealed partial class AppSettings
{
    public bool ExclusiveToolUnlocked { get; set; }
}
