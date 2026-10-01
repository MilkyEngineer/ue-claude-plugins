// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKit.Core;

namespace AgentKit.Horde;

/// <summary>Encrypts the cached token for this user; tests use a stand-in.</summary>
public interface IHordeTokenProtector
{
	/// <summary>Encrypts <paramref name="data"/>, bound to <paramref name="entropy"/>.</summary>
	byte[] Protect(byte[] data, byte[] entropy);

	/// <summary>Decrypts what <see cref="Protect"/> made with the same entropy; throws <see cref="CryptographicException"/> otherwise.</summary>
	byte[] Unprotect(byte[] data, byte[] entropy);
}

/// <summary>
/// uak's cache of Horde access tokens, one file per server: <c>&lt;UAK_HOME&gt;/horde/&lt;server&gt;/token.bin</c>. EpicGames.OIDC
/// keeps only refresh tokens, so when a server refuses refreshes every uak process would sign in again; this keeps the access
/// token itself until it expires, so back-to-back commands sign in once. Windows only: the file is encrypted with DPAPI for
/// the current user, with the server's URL as extra entropy. Elsewhere nothing is cached. The token is never printed or logged.
/// </summary>
public sealed class HordeTokenCache
{
	/// <summary>The cache file's name in the server's folder.</summary>
	public const string FileName = "token.bin";

	/// <summary>A cached token is used only while it has more than this left.</summary>
	public static readonly TimeSpan MinimumLife = TimeSpan.FromMinutes(5);

	readonly IHordeTokenProtector? _protector;

	/// <summary>Creates the cache under <paramref name="root"/> (the kit's <c>horde</c> folder); a null protector disables it.</summary>
	public HordeTokenCache(string root, IHordeTokenProtector? protector)
	{
		Root = Path.GetFullPath(root);
		_protector = protector;
	}

	/// <summary>The kit's <c>horde</c> folder.</summary>
	public string Root { get; }

	/// <summary>Whether tokens are cached at all (Windows only).</summary>
	public bool Enabled => _protector is not null;

	/// <summary>The clock; tests replace it.</summary>
	internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

	/// <summary>This user's cache: DPAPI on Windows, disabled elsewhere.</summary>
	public static HordeTokenCache ForUser(UakResolveOptions? options = null)
		=> new(HordeBuildSettingsStore.UserRoot(options), OperatingSystem.IsWindows() ? new DpapiTokenProtector() : null);

	/// <summary>The cache file of a server.</summary>
	public string PathFor(Uri server) => Path.Combine(Root, HordeBuildSettingsStore.ServerFolder(server), FileName);

	/// <summary>
	/// The cached token of a server, when there is one with more than <see cref="MinimumLife"/> left. A file that can't be read
	/// or decrypted, or that belongs to another server, is ignored (the next sign-in replaces it).
	/// </summary>
	public string? Read(Uri server)
	{
		if (_protector is null)
		{
			return null;
		}
		string path = PathFor(server);
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			byte[] plain = _protector.Unprotect(File.ReadAllBytes(path), Entropy(server));
			if (JsonNode.Parse(plain) is not JsonObject entry || Json.String(entry, "server") != server.ToString())
			{
				return null;
			}
			string? token = Json.String(entry, "accessToken");
			string? expires = Json.String(entry, "expires");
			if (string.IsNullOrEmpty(token) || !DateTime.TryParse(expires, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime expiry))
			{
				return null;
			}
			return expiry - UtcNow() > MinimumLife ? token : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException)
		{
			return null;
		}
	}

	/// <summary>
	/// Caches a server's token with its expiry, read from the token (a JWT's <c>exp</c> claim). Returns false, caching
	/// nothing, when the cache is disabled or the expiry isn't known. The write is atomic; the last writer wins.
	/// </summary>
	public bool Save(Uri server, string token)
	{
		DateTime? expiry = GetExpiry(token);
		if (_protector is null || expiry is null || expiry.Value <= UtcNow())
		{
			return false;
		}
		JsonObject entry = new()
		{
			["server"] = server.ToString(),
			["accessToken"] = token,
			["expires"] = expiry.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
		};
		StateFiles.WriteBytes(PathFor(server), _protector.Protect(Encoding.UTF8.GetBytes(entry.ToJsonString()), Entropy(server)));
		return true;
	}

	/// <summary>Deletes a server's cached token. Returns whether there was one.</summary>
	public bool Forget(Uri server)
	{
		string path = PathFor(server);
		if (!File.Exists(path))
		{
			return false;
		}
		File.Delete(path);
		return true;
	}

	/// <summary>A JWT's expiry (its <c>exp</c> claim, in UTC), or null when the token isn't a JWT or has none.</summary>
	public static DateTime? GetExpiry(string token)
	{
		string[] parts = token.Split('.');
		if (parts.Length != 3)
		{
			return null;
		}
		try
		{
			string payload = parts[1].Replace('-', '+').Replace('_', '/');
			payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
			if (JsonNode.Parse(Convert.FromBase64String(payload)) is JsonObject claims && Json.Get(claims, "exp") is JsonValue exp && exp.TryGetValue(out long seconds))
			{
				return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
			}
		}
		catch (Exception exception) when (exception is FormatException or JsonException or ArgumentOutOfRangeException or InvalidOperationException)
		{
		}
		return null;
	}

	static byte[] Entropy(Uri server) => Encoding.UTF8.GetBytes("uak-horde-token\n" + server);
}

/// <summary>DPAPI (CryptProtectData) for the current user, without a prompt.</summary>
internal sealed class DpapiTokenProtector : IHordeTokenProtector
{
	const int CryptProtectUiForbidden = 0x1;

	public byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, protect: true);

	public byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, protect: false);

	static byte[] Run(byte[] data, byte[] entropy, bool protect)
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("DPAPI is Windows only.");
		}
		GCHandle dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
		GCHandle entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
		DataBlob output = default;
		try
		{
			DataBlob input = new() { Size = data.Length, Data = dataHandle.AddrOfPinnedObject() };
			DataBlob extra = new() { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
			bool succeeded = protect
				? NativeMethods.CryptProtectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output)
				: NativeMethods.CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output);
			if (!succeeded)
			{
				throw new CryptographicException(Marshal.GetLastPInvokeError());
			}
			byte[] result = new byte[output.Size];
			Marshal.Copy(output.Data, result, 0, output.Size);
			return result;
		}
		finally
		{
			if (output.Data != IntPtr.Zero)
			{
				NativeMethods.LocalFree(output.Data);
			}
			dataHandle.Free();
			entropyHandle.Free();
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	struct DataBlob
	{
		public int Size;
		public IntPtr Data;
	}

	static class NativeMethods
	{
		[DllImport("crypt32.dll", SetLastError = true)]
		[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool CryptProtectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

		[DllImport("crypt32.dll", SetLastError = true)]
		[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

		[DllImport("kernel32.dll")]
		[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
		public static extern IntPtr LocalFree(IntPtr memory);
	}
}
