/*
 * Synthetic example runtime for the All-Stars mod loader: it does nothing.
 *
 * It exists so the example packages contain a real 32-bit DLL with the two entry points the loader looks
 * up (see docs/PACKAGE-FORMAT.md, section 4). It calls no game function, hooks nothing and links no
 * library. Released under the MIT license with the rest of this repository.
 */
#include <wchar.h>

/* HRESULT is a 32-bit signed value; 0 is S_OK. */
long __cdecl SsasrModInitialize(const void* host, const wchar_t* mod_directory)
{
    (void)host;
    (void)mod_directory;
    return 0;
}

long __cdecl SsasrModRollback(void)
{
    return 0;
}
