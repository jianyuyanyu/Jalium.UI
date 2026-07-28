#include "jalium_string_util.h"

#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <vector>

namespace {

int failures = 0;

void Check(bool condition, const char* message)
{
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

} // namespace

int main()
{
    const uint16_t shortText[] = {'o', 'k', 0};
    uint32_t length = 99;
    Check(jalium::ManagedUtf16LengthBounded(shortText, 3, &length) &&
              length == 2,
          "managed UTF-16 bounded scan finds the terminator");

    std::vector<uint16_t> unterminated(
        jalium::kMaxManagedFontFamilyCodeUnits, static_cast<uint16_t>('x'));
    length = 99;
    Check(!jalium::ManagedUtf16LengthBounded(
              unterminated.data(),
              jalium::kMaxManagedFontFamilyCodeUnits,
              &length) &&
              length == 0,
          "managed UTF-16 bounded scan rejects an unterminated font family");

    bool rejectedBeforeRead = false;
    try {
        (void)jalium::ManagedToWString(
            static_cast<const void*>(shortText),
            jalium::kMaxManagedTextCodeUnits + 1u);
    } catch (const std::length_error&) {
        rejectedBeforeRead = true;
    }
    Check(rejectedBeforeRead,
          "oversized managed text is rejected before reading its buffer");

    if (failures != 0) {
        std::cerr << failures << " string utility test(s) failed\n";
        return 1;
    }
    std::cout << "All managed string boundary tests passed\n";
    return 0;
}
