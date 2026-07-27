#include "jalium_audio.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <string>
#include <vector>

namespace {

void WriteLe16(std::ostream& output, uint16_t value)
{
    const std::array<char, 2> bytes = {
        static_cast<char>(value & 0xFFu),
        static_cast<char>((value >> 8) & 0xFFu)
    };
    output.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
}

void WriteLe32(std::ostream& output, uint32_t value)
{
    const std::array<char, 4> bytes = {
        static_cast<char>(value & 0xFFu),
        static_cast<char>((value >> 8) & 0xFFu),
        static_cast<char>((value >> 16) & 0xFFu),
        static_cast<char>((value >> 24) & 0xFFu)
    };
    output.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
}

bool GeneratePcmWav(const char* path, int seconds)
{
    constexpr uint32_t sampleRate = 48000;
    constexpr uint16_t channels = 2;
    constexpr uint16_t bitsPerSample = 16;
    constexpr uint32_t bytesPerFrame =
        channels * (bitsPerSample / 8u);
    const uint64_t dataBytes64 =
        static_cast<uint64_t>(seconds) * sampleRate * bytesPerFrame;
    if (seconds <= 0 ||
        dataBytes64 >
            static_cast<uint64_t>(std::numeric_limits<uint32_t>::max()) -
                36u) {
        return false;
    }
    const uint32_t dataBytes = static_cast<uint32_t>(dataBytes64);

    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    if (!output) return false;
    output.write("RIFF", 4);
    WriteLe32(output, 36u + dataBytes);
    output.write("WAVEfmt ", 8);
    WriteLe32(output, 16);
    WriteLe16(output, 1);
    WriteLe16(output, channels);
    WriteLe32(output, sampleRate);
    WriteLe32(output, sampleRate * bytesPerFrame);
    WriteLe16(output, static_cast<uint16_t>(bytesPerFrame));
    WriteLe16(output, bitsPerSample);
    output.write("data", 4);
    WriteLe32(output, dataBytes);

    const std::array<char, 1024 * 1024> silence{};
    uint32_t remaining = dataBytes;
    while (remaining != 0) {
        const size_t count =
            std::min<size_t>(remaining, silence.size());
        output.write(
            silence.data(),
            static_cast<std::streamsize>(count));
        if (!output) return false;
        remaining -= static_cast<uint32_t>(count);
    }
    return true;
}

double Percentile(
    const std::vector<double>& sorted,
    double percentile)
{
    const size_t index = static_cast<size_t>(
        std::ceil(percentile * static_cast<double>(sorted.size()))) - 1;
    return sorted[std::min(index, sorted.size() - 1)];
}

bool OpenOnce(const char* path, double* elapsedMs)
{
    const auto start = std::chrono::steady_clock::now();
    jalium_audio_decoder_t* decoder = nullptr;
    const jalium_media_status_t openStatus =
        jalium_audio_decoder_open_file(
            path,
            JALIUM_ACODEC_AUTO,
            &decoder);
    const auto end = std::chrono::steady_clock::now();
    if (openStatus != JALIUM_MEDIA_OK || !decoder) {
        std::cerr << "decoder open failed: status="
                  << static_cast<int>(openStatus) << '\n';
        return false;
    }

    jalium_audio_info_t info{};
    const jalium_media_status_t infoStatus =
        jalium_audio_decoder_get_info(decoder, &info);
    jalium_audio_decoder_close(decoder);
    if (infoStatus != JALIUM_MEDIA_OK ||
        info.sample_rate == 0 ||
        info.channels == 0) {
        std::cerr << "decoder info failed: status="
                  << static_cast<int>(infoStatus) << '\n';
        return false;
    }

    if (elapsedMs) {
        *elapsedMs =
            std::chrono::duration<double, std::milli>(end - start).count();
    }
    return true;
}

} // namespace

int main(int argc, char** argv)
{
    if (argc == 4 && std::string(argv[1]) == "--generate-wav") {
        const int seconds = std::atoi(argv[3]);
        if (!GeneratePcmWav(argv[2], seconds)) {
            std::cerr << "failed to generate PCM WAV fixture\n";
            return 1;
        }
        return 0;
    }

    if (argc < 2 || argc > 4) {
        std::cerr
            << "usage: jalium.native.media.audio-open-benchmark "
               "<audio-file> [iterations=20] [warmup=3]\n"
               "       jalium.native.media.audio-open-benchmark "
               "--generate-wav <output-file> <seconds>\n";
        return 2;
    }

    const int iterations = argc >= 3 ? std::atoi(argv[2]) : 20;
    const int warmup = argc >= 4 ? std::atoi(argv[3]) : 3;
    if (iterations < 2 || iterations > 1000 ||
        warmup < 0 || warmup > 1000) {
        std::cerr << "iteration and warmup counts are out of range\n";
        return 2;
    }

    const jalium_media_status_t initializeStatus =
        jalium_audio_initialize();
    if (initializeStatus != JALIUM_MEDIA_OK) {
        std::cerr << "audio initialization failed: status="
                  << static_cast<int>(initializeStatus) << '\n';
        return 1;
    }
    struct AudioShutdown {
        ~AudioShutdown() { jalium_audio_shutdown(); }
    } audioShutdown;

    for (int i = 0; i < warmup; ++i) {
        if (!OpenOnce(argv[1], nullptr)) return 1;
    }

    std::vector<double> samples;
    samples.reserve(static_cast<size_t>(iterations));
    for (int i = 0; i < iterations; ++i) {
        double elapsedMs = 0;
        if (!OpenOnce(argv[1], &elapsedMs)) return 1;
        samples.push_back(elapsedMs);
    }

    std::sort(samples.begin(), samples.end());
    const double median =
        samples.size() % 2 == 0
            ? (samples[samples.size() / 2 - 1] +
               samples[samples.size() / 2]) /
                  2.0
            : samples[samples.size() / 2];

    std::cout << std::fixed << std::setprecision(3)
              << "{\"iterations\":" << iterations
              << ",\"warmup\":" << warmup
              << ",\"median_ms\":" << median
              << ",\"p95_ms\":" << Percentile(samples, 0.95)
              << ",\"min_ms\":" << samples.front()
              << ",\"max_ms\":" << samples.back()
              << "}\n";
    return 0;
}
