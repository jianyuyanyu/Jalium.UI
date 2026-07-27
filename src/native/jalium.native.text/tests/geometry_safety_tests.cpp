#include "jalium_triangulate.h"

#include <cmath>
#include <iostream>
#include <limits>
#include <vector>

int main()
{
    int failures = 0;
    const auto check = [&](bool condition, const char* message) {
        if (!condition) {
            std::cerr << "FAIL: " << message << '\n';
            ++failures;
        }
    };

    std::vector<float> points;
    const float nan = std::numeric_limits<float>::quiet_NaN();
    jalium::FlattenCubicBezier(
        0, 0, nan, 10, 20, 10, 30, 0, points, nan);
    check(points.size() <= 2 &&
              (points.empty() ||
               (std::isfinite(points[0]) && std::isfinite(points[1]))),
          "non-finite cubic input is rejected without recursive expansion");

    points.clear();
    jalium::FlattenQuadraticBezier(
        0, 0, 10, nan, 20, 0, points, -1.0f);
    check(points.size() <= 2 &&
              (points.empty() ||
               (std::isfinite(points[0]) && std::isfinite(points[1]))),
          "non-finite quadratic input is rejected without recursive expansion");

    points.clear();
    const float maximum = std::numeric_limits<float>::max();
    jalium::FlattenCubicBezier(
        -maximum, 0, maximum, maximum,
        -maximum, -maximum, maximum, 0,
        points, std::numeric_limits<float>::denorm_min());
    check(points.size() <= (2u << 16),
          "finite extreme cubic is bounded by the recursion limit");
    bool allFinite = true;
    for (float value : points) allFinite &= std::isfinite(value);
    check(allFinite, "curve flattening never emits non-finite coordinates");

    if (failures != 0) {
        std::cerr << failures << " geometry safety test(s) failed\n";
        return 1;
    }
    std::cout << "All geometry safety tests passed\n";
    return 0;
}
