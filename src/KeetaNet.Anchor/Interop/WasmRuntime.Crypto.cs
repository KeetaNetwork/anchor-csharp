using System.Buffers.Binary;
using System.Text;

namespace KeetaNet.Anchor;

/// <summary>
/// The offline <c>crypto</c> surface of the P1 core module: handle-based account,
/// base certificate, and KYC certificate objects. Every internal entry point
/// dispatches onto the runtime's owner thread.
/// </summary>
public sealed partial class WasmRuntime
{
	internal int AccountFromSeed(string seed, uint index, string algorithm) =>
		DerivedAccount("keeta_account_from_seed", seed, index, algorithm);

	internal int AccountFromAddress(string address) => ParseText("keeta_account_from_address", address);

	internal int AccountFromPrivateKey(string privateKey, string algorithm) =>
		AccountFromKeyMaterial("keeta_account_from_private_key", privateKey, algorithm);

	internal int AccountFromPassphrase(string words, uint index, string algorithm) =>
		DerivedAccount("keeta_account_from_passphrase", words, index, algorithm);

	internal int AccountFromPublicKey(string publicKey, string algorithm) =>
		AccountFromKeyMaterial("keeta_account_from_public_key", publicKey, algorithm);

	/// <summary>Derive an account from a secret (seed or mnemonic) at a key index.</summary>
	private int DerivedAccount(string export, string secret, uint index, string algorithm) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument material = arguments.Write(secret);
			Argument algo = arguments.Write(algorithm);

			int result = Invoke<int, int, int, int, int, int>(export, material.Pointer, material.Length, (int)index, algo.Pointer, algo.Length);
			return TakeHandle(result);
		});

	/// <summary>Build an account from hex key material and an algorithm name.</summary>
	private int AccountFromKeyMaterial(string export, string keyMaterial, string algorithm) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument key = arguments.Write(keyMaterial);
			Argument algo = arguments.Write(algorithm);

			int result = Invoke<int, int, int, int, int>(export, key.Pointer, key.Length, algo.Pointer, algo.Length);
			return TakeHandle(result);
		});

	internal string AccountGenerateSeed() => GeneratedText("keeta_generate_seed");

	internal string AccountGeneratePassphrase() => GeneratedText("keeta_generate_passphrase");

	/// <summary>Drive a zero-argument generator export that yields UTF-8 text.</summary>
	private string GeneratedText(string export) =>
		Run(() =>
		{
			int result = Invoke<int>(export);
			return Text(result);
		});

	internal byte[] AccountEncrypt(int handle, byte[] plaintext) =>
		WithHandleAndBytes("keeta_account_encrypt", handle, plaintext);

	internal byte[] AccountDecrypt(int handle, byte[] ciphertext) =>
		WithHandleAndBytes("keeta_account_decrypt", handle, ciphertext);

	internal string AccountAddress(int handle) => TextOf("keeta_account_address", handle);

	internal string AccountAlgorithm(int handle) => TextOf("keeta_account_algorithm", handle);

	internal string AccountPublicKey(int handle) => TextOf("keeta_account_public_key", handle);

	internal byte[] AccountSign(int handle, byte[] message) =>
		WithHandleAndBytes("keeta_account_sign", handle, message);

	internal bool AccountVerify(int handle, byte[] message, byte[] signature) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument body = arguments.WriteBytes(message);
			Argument sig = arguments.WriteBytes(signature);

			int result = Invoke<int, int, int, int, int, int>("keeta_account_verify", handle, body.Pointer, body.Length, sig.Pointer, sig.Length);
			return result != 0;
		});

	internal void AccountFree(int handle) => RunFree("keeta_account_free", handle);

	internal int CertificateParse(string pem) => ParseText("keeta_certificate_parse", pem);

	internal int CertificateParseDer(byte[] der) => ParseBytes("keeta_certificate_parse_der", der);

	internal string CertificatePem(int handle) => TextOf("keeta_certificate_pem", handle);

	internal byte[] CertificateDer(int handle) => BytesOf("keeta_certificate_der", handle);

	internal bool CertificateValidAt(int handle, long unixMillis) =>
		ValidAt("keeta_certificate_valid_at", handle, unixMillis);

	internal string CertificateSubject(int handle) => TextOf("keeta_certificate_subject", handle);

	internal string CertificateIssuer(int handle) => TextOf("keeta_certificate_issuer", handle);

	internal string CertificateSerial(int handle) => TextOf("keeta_certificate_serial", handle);

	internal long CertificateNotBefore(int handle) => LongOf("keeta_certificate_not_before", handle);

	internal long CertificateNotAfter(int handle) => LongOf("keeta_certificate_not_after", handle);

	internal string CertificateSubjectPublicKey(int handle) =>
		TextOf("keeta_certificate_subject_public_key", handle);

	internal void CertificateFree(int handle) => RunFree("keeta_certificate_free", handle);

	internal int KycCertificateParse(string pem) => ParseText("keeta_kyc_certificate_parse", pem);

	internal int KycCertificateBase(int handle) => HandleOf("keeta_kyc_certificate_base", handle);

	internal bool KycCertificateValidAt(int handle, long unixMillis) =>
		ValidAt("keeta_kyc_certificate_valid_at", handle, unixMillis);

	internal bool KycCertificateVerify(int handle, int[] trustedRoots, int[] intermediates, long unixMillis) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument roots = arguments.WriteHandles(trustedRoots);
			Argument bridges = arguments.WriteHandles(intermediates);

			int result = Invoke<int, int, int, int, int, long, int>("keeta_kyc_certificate_verify", handle, roots.Pointer, roots.Length, bridges.Pointer, bridges.Length, unixMillis);
			return TakeFlag(result);
		});

	internal byte[] KycCertificateAttributes(int handle) =>
		BytesOf("keeta_kyc_certificate_attributes", handle);

	internal byte[] KycCertificatePlainAttribute(int handle, string name) =>
		Run(() => WithHandleAndText("keeta_kyc_certificate_plain_attribute", handle, name));

	internal byte[] KycCertificateDecryptAttribute(int handle, string name, int accountHandle) =>
		NamedAttributeWithAccount("keeta_kyc_certificate_decrypt_attribute", handle, name, accountHandle);

	internal byte[] KycCertificateProve(int handle, string name, int accountHandle) =>
		NamedAttributeWithAccount("keeta_kyc_certificate_prove", handle, name, accountHandle);

	private byte[] NamedAttributeWithAccount(string export, int handle, string name, int accountHandle) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument label = arguments.Write(name);

			int result = Invoke<int, int, int, int, int>(export, handle, label.Pointer, label.Length, accountHandle);
			return TakeBytes(result);
		});

	internal bool KycCertificateValidateProof(int handle, string name, int accountHandle, string proof) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument label = arguments.Write(name);
			Argument document = arguments.Write(proof);

			int result = Invoke<int, int, int, int, int, int, int>("keeta_kyc_certificate_validate_proof", handle, label.Pointer, label.Length, accountHandle, document.Pointer, document.Length);
			return TakeFlag(result);
		});

	internal int KycCertificateIssue(int subjectHandle, int issuerHandle, string parameters) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument args = arguments.Write(parameters);

			int result = Invoke<int, int, int, int, int>("keeta_kyc_certificate_issue", subjectHandle, issuerHandle, args.Pointer, args.Length);
			return TakeHandle(result);
		});

	internal string KycCertificatePem(int handle) => TextOf("keeta_kyc_certificate_pem", handle);

	internal void KycCertificateFree(int handle) => RunFree("keeta_kyc_certificate_free", handle);

	internal int KycWithAccount(string nodeUrl, string root, int accountHandle) =>
		ClientWithAccount("keeta_kyc_with_account", nodeUrl, root, accountHandle);

	/// <summary>Drive a validity-window predicate at a Unix-milliseconds moment.</summary>
	private bool ValidAt(string export, int handle, long unixMillis) =>
		Run(() =>
		{
			int result = Invoke<int, long, int>(export, handle, unixMillis);
			return TakeFlag(result);
		});

	/// <summary>Parse a UTF-8 textual argument into an object, returning its handle.</summary>
	private int ParseText(string export, string value) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument argument = arguments.Write(value);

			int result = Invoke<int, int, int>(export, argument.Pointer, argument.Length);
			return TakeHandle(result);
		});

	/// <summary>Parse a binary argument into an object, returning its handle.</summary>
	private int ParseBytes(string export, byte[] value) =>
		Run(() =>
		{
			using var arguments = new ArgumentScope(this);
			Argument argument = arguments.WriteBytes(value);

			int result = Invoke<int, int, int>(export, argument.Pointer, argument.Length);
			return TakeHandle(result);
		});

	/// <summary>The UTF-8 text a bytes handle carries.</summary>
	private string Text(int handle)
	{
		byte[] value = TakeBytes(handle);
		return Encoding.UTF8.GetString(value);
	}

	/// <summary>Copy raw bytes into a fresh guest buffer.</summary>
	private Argument WriteBytes(byte[] value, List<Argument> owned)
	{
		EnsureUsable();
		int pointer = _alloc(value.Length);

		// Register the allocation before touching guest memory so a copy
		// failure cannot leak the buffer.
		var argument = new Argument(pointer, value.Length);
		owned.Add(argument);

		if (value.Length > 0)
		{
			value.AsSpan().CopyTo(_memory.GetSpan(pointer, value.Length));
		}

		return argument;
	}

	/// <summary>Copy a list of handles into a fresh guest buffer of little-endian i32.</summary>
	private Argument WriteHandles(int[] handles, List<Argument> owned)
	{
		byte[] buffer = new byte[handles.Length * sizeof(int)];
		for (int index = 0; index < handles.Length; index++)
		{
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(index * sizeof(int)), handles[index]);
		}

		return WriteBytes(buffer, owned);
	}

	/// <summary>Resolve `export` after the liveness and thread-confinement checks.</summary>
	private TFunction Export<TFunction>(string export, TFunction? function) where TFunction : class
	{
		EnsureUsable();
		return Required(export, function);
	}

	private int Invoke<TResult>(string export)
	{
		var function = _instance.GetFunction<TResult>(export);
		var resolved = Export(export, function);
		return (int)(object)resolved()!;
	}

	private int Invoke<T1, TResult>(string export, T1 arg1)
	{
		var function = _instance.GetFunction<T1, TResult>(export);
		var resolved = Export(export, function);

		return (int)(object)resolved(arg1)!;
	}

	private long InvokeLong<T1>(string export, T1 arg1)
	{
		var function = _instance.GetFunction<T1, long>(export);
		var resolved = Export(export, function);

		return resolved(arg1);
	}

	private int Invoke<T1, T2, TResult>(string export, T1 arg1, T2 arg2)
	{
		var function = _instance.GetFunction<T1, T2, TResult>(export);
		var resolved = Export(export, function);

		return (int)(object)resolved(arg1, arg2)!;
	}

	private int Invoke<T1, T2, T3, TResult>(string export, T1 arg1, T2 arg2, T3 arg3)
	{
		var function = _instance.GetFunction<T1, T2, T3, TResult>(export);
		var resolved = Export(export, function);

		return (int)(object)resolved(arg1, arg2, arg3)!;
	}

	private int Invoke<T1, T2, T3, T4, TResult>(string export, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
	{
		var function = _instance.GetFunction<T1, T2, T3, T4, TResult>(export);
		var resolved = Export(export, function);

		return (int)(object)resolved(arg1, arg2, arg3, arg4)!;
	}

	private int Invoke<T1, T2, T3, T4, T5, TResult>(string export, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
	{
		var function = _instance.GetFunction<T1, T2, T3, T4, T5, TResult>(export);
		var resolved = Export(export, function);

		return (int)(object)resolved(arg1, arg2, arg3, arg4, arg5)!;
	}

	private int Invoke<T1, T2, T3, T4, T5, T6, TResult>(
		string export, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6)
	{
		var function = _instance.GetFunction<T1, T2, T3, T4, T5, T6, TResult>(export);
		var resolved = Export(export, function);

		return (int)(object)resolved(arg1, arg2, arg3, arg4, arg5, arg6)!;
	}

	private int Invoke<T1, T2, T3, T4, T5, T6, T7, TResult>(
		string export, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7)
	{
		var function = _instance.GetFunction<T1, T2, T3, T4, T5, T6, T7, TResult>(export);
		var resolved = Export(export, function);

		return (int)(object)resolved(arg1, arg2, arg3, arg4, arg5, arg6, arg7)!;
	}

	/// <summary>Release a guest handle on the dispatcher thread (see <see cref="RunFree"/>).</summary>
	private void Free(string export, int handle)
	{
		Action<int>? action = _instance.GetAction<int>(export);
		Action<int> resolved = Export(export, action);

		resolved(handle);
	}
}
