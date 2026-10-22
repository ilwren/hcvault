/*
 Derived from source code of TrueCrypt 7.1a, which is
 Copyright (c) 2008-2012 TrueCrypt Developers Association and which is governed
 by the TrueCrypt License 3.0.

 Modifications and additions to the original source code (contained in this file)
 and all other portions of this file are Copyright (c) 2013-2026 AM Crypto
 and are governed by the Apache License 2.0 the full text of which is
 contained in the file License.txt included in VeraCrypt binary and source
 code distribution packages.
*/

#ifndef TC_HEADER_Encryption_EncryptionTest
#define TC_HEADER_Encryption_EncryptionTest

#include "Platform/Platform.h"
#include "Common/Crypto.h"

namespace VeraCrypt
{
	class EncryptionTest
	{
	public:
		static void TestAll ();
		static void TestAll (bool enableCpuEncryptionSupport);

		//  The Argon2id self-test derives two keys at PIM 1, i.e. two
		//  64 MiB / 3-iteration passes. Running it eagerly from TestAll()
		//  cost every process ~64 MiB and several hundred milliseconds at
		//  startup even when the caller only ever uses HMAC-SHA-512, so it
		//  is deferred until Argon2 is actually used: Pkcs5Argon2::DeriveKey
		//  calls EnsureArgon2Tested() before deriving anything. Validation
		//  is unchanged - no Argon2 key is ever returned to a caller before
		//  the test vectors have been verified on this machine.
		static void EnsureArgon2Tested ();

	protected:
		static void TestCiphers ();
		static void TestLegacyModes ();
		static void TestPkcs5 ();
		static void TestArgon2 ();
		static void TestXts ();
		static void TestXtsAES ();

	struct XtsTestVector
	{
		uint8 key1[32];
		uint8 key2[32];
		uint8 dataUnitNo[8];
		unsigned int blockNo;
		uint8 plaintext[ENCRYPTION_DATA_UNIT_SIZE];
		uint8 ciphertext[ENCRYPTION_DATA_UNIT_SIZE];
	};

	static const XtsTestVector XtsTestVectors[];

	private:
		EncryptionTest ();
		virtual ~EncryptionTest ();
		EncryptionTest (const EncryptionTest &);
		EncryptionTest &operator= (const EncryptionTest &);
	};
}

#endif // TC_HEADER_Encryption_EncryptionTest
