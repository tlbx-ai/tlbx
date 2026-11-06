/* Build-time load/export check only. It never calls RTC or creates a peer/socket. */
#include <stdio.h>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
int wmain(int argc, wchar_t **argv) {
    if (argc < 3) return 2;
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    HMODULE library = LoadLibraryExW(argv[1], NULL,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!library) { fprintf(stderr, "Native load failed: %lu\n", GetLastError()); return 3; }
    for (int i = 2; i < argc; ++i) {
        char name[128];
        if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, argv[i], -1, name, sizeof(name), NULL, NULL)
            || !GetProcAddress(library, name)) {
            fprintf(stderr, "Missing exact C API export at index %d\n", i - 2); return 4;
        }
    }
    FreeLibrary(library);
#else
#include <dlfcn.h>
int main(int argc, char **argv) {
    if (argc < 3) return 2;
    void *library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!library) { fprintf(stderr, "Native load failed: %s\n", dlerror()); return 3; }
    for (int i = 2; i < argc; ++i) {
        if (!dlsym(library, argv[i])) { fprintf(stderr, "Missing C API export: %s\n", argv[i]); return 4; }
    }
    dlclose(library);
#endif
    printf("PASS: native load and %d exact C API exports; no peer or socket created.\n", argc - 2);
    return 0;
}
