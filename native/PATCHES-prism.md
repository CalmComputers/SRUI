# Local Patches to Vendored Prism

The `prism/` directory is a vendored snapshot of
https://github.com/ethindp/prism with the following local changes. Re-apply
(or retire) them when updating the snapshot.

# 1. frozen_registry.cpp: std::flat_set → std::set

`prism/source/frozen_registry.cpp` includes `<flat_set>`, which the MSVC
STL does not ship as of VS 17.14, and SRUI builds Prism with
clang-cl against the MSVC STL. `std::set` is a drop-in replacement at this
call site (a uniqueness check during registry construction). Retire the
patch once the MSVC STL gains `<flat_set>`.
