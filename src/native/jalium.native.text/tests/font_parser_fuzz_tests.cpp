#include "font_face.h"
#include "cff_charstring.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <iterator>
#include <limits>
#include <string>
#include <vector>

namespace {

constexpr size_t kMaxSeedBytes = 64u * 1024u * 1024u;

uint32_t NextRandom(uint32_t& state)
{
    state ^= state << 13;
    state ^= state >> 17;
    state ^= state << 5;
    return state;
}

bool ReadSeed(const char* path, std::vector<uint8_t>& bytes)
{
    if (!path || !*path) return false;
    std::ifstream input(path, std::ios::binary | std::ios::ate);
    if (!input) return false;
    const std::streamoff size = input.tellg();
    if (size <= 0 ||
        static_cast<uint64_t>(size) > static_cast<uint64_t>(kMaxSeedBytes)) {
        return false;
    }
    input.seekg(0, std::ios::beg);
    bytes.resize(static_cast<size_t>(size));
    return input.read(
        reinterpret_cast<char*>(bytes.data()),
        static_cast<std::streamsize>(bytes.size())).good();
}

bool Exercise(std::vector<uint8_t> bytes)
{
    auto face = jalium::FontFace::Parse(std::move(bytes), 0);
    if (!face) return true;

    const std::array<uint32_t, 8> codepoints = {
        0u, 0x20u, 0x41u, 0x7Fu, 0x7FFu, 0xFFFFu, 0x10000u, 0x10FFFFu
    };
    for (uint32_t codepoint : codepoints) {
        (void)face->GetGlyphIndex(codepoint);
    }

    const std::array<uint16_t, 3> glyphs = {
        0u,
        static_cast<uint16_t>(face->NumGlyphs() > 1 ? 1 : 0),
        static_cast<uint16_t>(face->NumGlyphs() - 1)
    };
    for (uint16_t glyph : glyphs) {
        jalium::GlyphOutline outline;
        (void)face->GetGlyphContours(glyph, 1.0f, outline);
    }
    return true;
}

void ExerciseMalformedCffDicts()
{
    // Minimal CFF1 container whose Top DICT encodes an overflowing BCD real as
    // the CharStrings offset.  The parser must reject it without a non-finite
    // floating-point-to-uint conversion.
    const std::vector<uint8_t> nonFiniteOffset = {
        1, 0, 4, 4,                    // CFF header
        0, 1, 1, 1, 2, 'A',           // Name INDEX
        0, 1, 1, 1, 6,                // Top DICT INDEX header
        30, 0x1B, 0x99, 0x9F, 17,     // 1E999, CharStrings operator
        0, 0,                          // String INDEX
        0, 0                           // Global Subr INDEX
    };
    jalium::font::CffFontProgram program;
    (void)program.Parse(
        jalium::font::ByteReader(nonFiniteOffset), 1, false);

    // Negative CharStrings offset (-108) exercises the same unsigned
    // conversion boundary with a finite but out-of-domain operand.
    const std::vector<uint8_t> negativeOffset = {
        1, 0, 4, 4,
        0, 1, 1, 1, 2, 'A',
        0, 1, 1, 1, 4,
        251, 0, 17,
        0, 0,
        0, 0
    };
    (void)program.Parse(
        jalium::font::ByteReader(negativeOffset), 1, false);
}

} // namespace

int main(int argc, char** argv)
{
    size_t cases = 0;
    bool success = true;
    const auto run = [&](std::vector<uint8_t> bytes) {
        ++cases;
        try {
            success &= Exercise(std::move(bytes));
        } catch (const std::exception& error) {
            std::cerr << "FAIL: parser case " << cases
                      << " threw: " << error.what() << '\n';
            success = false;
        } catch (...) {
            std::cerr << "FAIL: parser case " << cases
                      << " threw a non-standard exception\n";
            success = false;
        }
    };

    ExerciseMalformedCffDicts();

    // Truncated and random sfnt-shaped inputs cover all ByteReader boundaries
    // even on a machine without an installed seed font.
    for (size_t length = 0; length <= 256; ++length) {
        std::vector<uint8_t> bytes(length, 0);
        if (length >= 4) {
            bytes[0] = 0x00;
            bytes[1] = 0x01;
            bytes[2] = 0x00;
            bytes[3] = 0x00;
        }
        run(std::move(bytes));
    }

    uint32_t random = 0xC0DEC0DEu;
    for (size_t index = 0; index < 1024; ++index) {
        const size_t length = 12u + (NextRandom(random) % 4085u);
        std::vector<uint8_t> bytes(length);
        for (uint8_t& value : bytes) {
            value = static_cast<uint8_t>(NextRandom(random));
        }
        bytes[0] = 0x00;
        bytes[1] = 0x01;
        bytes[2] = 0x00;
        bytes[3] = 0x00;
        run(std::move(bytes));
    }

    std::vector<uint8_t> seed;
    const char* seedPath = argc > 1 ? argv[1] : nullptr;
    if (!ReadSeed(seedPath, seed)) {
        seedPath = std::getenv("JALIUM_FONT_FUZZ_SEED");
        (void)ReadSeed(seedPath, seed);
    }
    if (seed.empty()) {
        (void)ReadSeed(
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", seed);
    }

    if (!seed.empty()) {
        run(seed);

        for (size_t index = 0; index < 256; ++index) {
            const size_t length =
                index * (seed.size() - 1u) / 255u;
            run(std::vector<uint8_t>(seed.begin(), seed.begin() + length));
        }

        for (size_t index = 0; index < 768; ++index) {
            std::vector<uint8_t> mutated = seed;
            const size_t mutations = 1u + (NextRandom(random) % 8u);
            for (size_t mutation = 0; mutation < mutations; ++mutation) {
                const size_t position =
                    NextRandom(random) % mutated.size();
                mutated[position] ^=
                    static_cast<uint8_t>(1u << (NextRandom(random) & 7u));
            }
            run(std::move(mutated));
        }

        for (size_t index = 0; index < 256; ++index) {
            std::vector<uint8_t> mutated = seed;
            const size_t position =
                NextRandom(random) % std::min<size_t>(mutated.size(), 65536u);
            const uint8_t fill = (index & 1u) ? 0xFFu : 0x00u;
            const size_t count = std::min<size_t>(4u, mutated.size() - position);
            std::fill_n(mutated.begin() + position, count, fill);
            run(std::move(mutated));
        }
    } else {
        std::cout << "SKIP: no installed seed font; random corpus still ran\n";
    }

    if (!success) return 1;
    std::cout << "Font parser boundary corpus passed: "
              << cases << " cases\n";
    return 0;
}
