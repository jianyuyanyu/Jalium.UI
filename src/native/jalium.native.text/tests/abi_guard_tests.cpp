#include "jalium_abi_guard.h"

#include <iostream>
#include <stdexcept>

namespace {

struct ThrowingTarget {
    bool pushed = false;
    bool popped = false;

    void PushTransform(const float*)
    {
        pushed = true;
    }

    void PopTransform()
    {
        popped = true;
        pushed = false;
    }
};

} // namespace

int main()
{
    int failures = 0;
    const auto check = [&](bool condition, const char* message) {
        if (!condition) {
            std::cerr << "FAIL: " << message << '\n';
            ++failures;
        }
    };

    const char terminated[] = "shader";
    uint32_t length = 99;
    check(jalium::CStringLengthBounded(
              terminated, sizeof(terminated), &length) &&
              length == 6,
          "bounded byte-string scan finds a terminator");

    const char unterminated[] = {'x', 'y', 'z'};
    length = 99;
    check(!jalium::CStringLengthBounded(
              unterminated, sizeof(unterminated), &length) &&
              length == 0,
          "bounded byte-string scan rejects an unterminated source");

    ThrowingTarget target;
    const float transform[6] = {1, 0, 0, 1, 3, 4};
    jalium::InvokeWithOptionalTransformNoexcept(
        &target, transform, [] { throw std::runtime_error("injected"); });
    check(target.popped && !target.pushed,
          "transient transform is restored when a backend draw throws");

    ThrowingTarget noTransformTarget;
    jalium::InvokeWithOptionalTransformNoexcept(
        &noTransformTarget, nullptr,
        [] { throw std::runtime_error("injected"); });
    check(!noTransformTarget.pushed && !noTransformTarget.popped,
          "a failed untransformed draw does not mutate transform state");

    if (failures != 0) {
        std::cerr << failures << " ABI guard test(s) failed\n";
        return 1;
    }
    std::cout << "All C ABI guard tests passed\n";
    return 0;
}
