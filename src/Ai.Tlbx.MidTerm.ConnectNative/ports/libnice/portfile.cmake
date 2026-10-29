vcpkg_download_distfile(ARCHIVE
    URLS "https://libnice.freedesktop.org/releases/libnice-${VERSION}.tar.gz"
    FILENAME "libnice-${VERSION}.tar.gz"
    SHA512 ab4be35b2bcf19b303e4d3fcde9f5b8423169e921ae3453018966b22e8467d81d86494980b41d738f3ed9c7da383a6a2281f61fff9a726b9af4451712ba85d17
)
vcpkg_extract_source_archive(SOURCE_PATH ARCHIVE "${ARCHIVE}")
vcpkg_configure_meson(
    SOURCE_PATH "${SOURCE_PATH}"
    OPTIONS
        -Dgtk_doc=disabled #Enable generating the API reference (depends on GTK-Doc)
        -Dintrospection=disabled #Enable GObject Introspection (depends on GObject)'
        -Dtests=disabled
        -Dexamples=disabled
        -Dgstreamer=disabled
        -Dgupnp=disabled
        -Dcrypto-library=openssl
    ADDITIONAL_BINARIES glib-genmarshal='${CURRENT_INSTALLED_DIR}/tools/glib/glib-genmarshal'
                        glib-mkenums='${CURRENT_INSTALLED_DIR}/tools/glib/glib-mkenums'
)

# Could be features:
# option('gupnp', type: 'feature', value: 'auto',
  # description: 'Enable or disable GUPnP IGD support')
# option('ignored-network-interface-prefix', type: 'array', value: ['docker', 'veth', 'virbr', 'vnet'],
  # description: 'Ignore network interfaces whose name starts with a string from this list in the ICE connection check algorithm. For example, "virbr" to ignore virtual bridge interfaces added by virtd, which do not help in finding connectivity.')
# option('crypto-library', type: 'combo', choices : ['auto', 'gnutls', 'openssl'], value : 'auto')

vcpkg_install_meson()

vcpkg_copy_pdbs()
vcpkg_copy_tools(TOOL_NAMES stunbdc stund AUTO_CLEAN)
vcpkg_fixup_pkgconfig()

vcpkg_install_copyright(
  FILE_LIST
    "${SOURCE_PATH}/COPYING"
    "${SOURCE_PATH}/COPYING.LGPL"
    "${SOURCE_PATH}/COPYING.MPL"
)
