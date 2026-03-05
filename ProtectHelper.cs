using System;
using System.Security.Cryptography;
using System.Text;

namespace edge_runtime
{
    public static class ProtectHelper
    {
        // 使用 DPAPI，Machine scope，以便任何登录用户在同一机器上都可解密
        public static string EncryptString(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            var bytes = Encoding.UTF8.GetBytes(plain);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(encrypted);
        }

        public static string DecryptString(string encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64)) return string.Empty;
            try
            {
                var bytes = Convert.FromBase64String(encryptedBase64);
                var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
