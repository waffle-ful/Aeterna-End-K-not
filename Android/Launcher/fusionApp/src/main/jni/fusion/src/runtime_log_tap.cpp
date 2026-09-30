#include <runtime_log_tap.h>
#include <blackbox.h>
#include <logger.h>
#include <android/log.h>
#include <link.h>
#include <sys/mman.h>
#include <unistd.h>
#include <cerrno>
#include <cstdint>
#include <cstring>
#include <string_view>

#define TAG "FusionRuntimeLog"

namespace
{
    constexpr std::string_view RUNTIME_LIBRARY = "libcoreclr.so";
    constexpr const char *IMPORT_NAME = "__android_log_write";

    int write_ring_then_log(int priority, const char *tag, const char *text)
    {
        blackbox_write_tagged(tag ? tag : "runtime", text);
        return __android_log_write(priority, tag, text);
    }

#if defined(__aarch64__)
    struct Search
    {
        bool library_seen;
        ElfW(Addr) *slot;
        // The part of the library that the loader made read-only after relocating it.
        uintptr_t read_only_begin;
        uintptr_t read_only_end;
    };

    // The addresses in the dynamic section are as written in the file; the load bias turns them
    // into addresses in this process.
    int find_import(dl_phdr_info *info, size_t, void *data)
    {
        const std::string_view name = info->dlpi_name ? info->dlpi_name : "";
        if (!name.ends_with(RUNTIME_LIBRARY))
        {
            return 0;
        }

        auto *search = static_cast<Search *>(data);
        search->library_seen = true;

        const ElfW(Dyn) *dynamic = nullptr;
        for (int i = 0; i < info->dlpi_phnum; i++)
        {
            const ElfW(Phdr) &header = info->dlpi_phdr[i];
            if (header.p_type == PT_DYNAMIC)
            {
                dynamic = reinterpret_cast<const ElfW(Dyn) *>(info->dlpi_addr + header.p_vaddr);
            }
            else if (header.p_type == PT_GNU_RELRO)
            {
                search->read_only_begin = info->dlpi_addr + header.p_vaddr;
                search->read_only_end = search->read_only_begin + header.p_memsz;
            }
        }
        if (!dynamic)
        {
            return 1;
        }

        const ElfW(Rela) *relocations = nullptr;
        size_t relocations_size = 0;
        const ElfW(Sym) *symbols = nullptr;
        const char *strings = nullptr;
        bool rela = false;
        for (const ElfW(Dyn) *entry = dynamic; entry->d_tag != DT_NULL; entry++)
        {
            switch (entry->d_tag)
            {
                case DT_JMPREL:
                    relocations = reinterpret_cast<const ElfW(Rela) *>(info->dlpi_addr + entry->d_un.d_ptr);
                    break;
                case DT_PLTRELSZ:
                    relocations_size = entry->d_un.d_val;
                    break;
                case DT_PLTREL:
                    rela = entry->d_un.d_val == DT_RELA;
                    break;
                case DT_SYMTAB:
                    symbols = reinterpret_cast<const ElfW(Sym) *>(info->dlpi_addr + entry->d_un.d_ptr);
                    break;
                case DT_STRTAB:
                    strings = reinterpret_cast<const char *>(info->dlpi_addr + entry->d_un.d_ptr);
                    break;
                default:
                    break;
            }
        }
        if (!relocations || !symbols || !strings || !rela)
        {
            return 1;
        }

        const size_t count = relocations_size / sizeof(ElfW(Rela));
        for (size_t i = 0; i < count; i++)
        {
            const ElfW(Rela) &relocation = relocations[i];
            if (ELF64_R_TYPE(relocation.r_info) != R_AARCH64_JUMP_SLOT)
            {
                continue;
            }

            const ElfW(Sym) &symbol = symbols[ELF64_R_SYM(relocation.r_info)];
            if (strcmp(strings + symbol.st_name, IMPORT_NAME) == 0)
            {
                search->slot = reinterpret_cast<ElfW(Addr) *>(info->dlpi_addr + relocation.r_offset);
                break;
            }
        }
        return 1;
    }
#endif
}

void runtime_log_tap_install()
{
#if defined(__aarch64__)
    static bool installed = false;
    if (installed)
    {
        return;
    }

    Search search{};
    dl_iterate_phdr(find_import, &search);
    if (!search.slot)
    {
        log_format(LogLevel::WARN, TAG, "The runtime's log lines are not kept: {}",
                   search.library_seen ? "its import of the log function was not found" : "its library is not loaded");
        return;
    }

    // The slot must already hold the real function: anything else means the import table is
    // not laid out as assumed, or somebody else has redirected it, and it is left alone.
    const auto real = reinterpret_cast<ElfW(Addr)>(&__android_log_write);
    if (*search.slot != real)
    {
        log(LogLevel::WARN, TAG, "The runtime's log lines are not kept: its import does not point at the log function");
        return;
    }

    // Where the loader made the import table read-only, its page is opened for the one store
    // and closed again. Elsewhere the table is ordinary writable data and is left as it is.
    const auto slot_address = reinterpret_cast<uintptr_t>(search.slot);
    const bool read_only = slot_address >= search.read_only_begin && slot_address < search.read_only_end;
    const long page_size = sysconf(_SC_PAGESIZE);
    const auto page = reinterpret_cast<void *>(slot_address & ~static_cast<uintptr_t>(page_size - 1));
    if (read_only && (page_size <= 0 || mprotect(page, static_cast<size_t>(page_size), PROT_READ | PROT_WRITE) != 0))
    {
        log_format(LogLevel::WARN, TAG, "The runtime's log lines are not kept: cannot open the import table: {}", strerror(errno));
        return;
    }

    __atomic_store_n(search.slot, reinterpret_cast<ElfW(Addr)>(&write_ring_then_log), __ATOMIC_RELEASE);
    if (read_only && mprotect(page, static_cast<size_t>(page_size), PROT_READ) != 0)
    {
        log_format(LogLevel::WARN, TAG, "The import table page stays writable: {}", strerror(errno));
    }
    installed = true;
    log(LogLevel::INFO, TAG, "The runtime's log lines are copied to the ring");
#endif
}
