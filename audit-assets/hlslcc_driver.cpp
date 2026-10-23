#include "hlslcc.h"
#include <fstream>
#include <iostream>
class Reflection : public HLSLccReflection {
  public:
    void OnDiagnostics(const std::string &error, int line, bool isError) override {
        std::cerr << (isError ? "error" : "note") << ':' << line << ':' << error << '\n';
    }
};
int main(int argc, char **argv) {
    if (argc != 3) {
        std::cerr << "usage: hlslcc_driver input.dxbc output.metal\n";
        return 2;
    }
    GLSLCrossDependencyData dependencies;
    HLSLccSamplerPrecisionInfo precision;
    Reflection reflection;
    GLSLShader shader;
    int ok = TranslateHLSLFromFile(argv[1], 0, LANG_METAL, nullptr, &dependencies, precision,
                                   reflection, &shader);
    if (!ok) {
        std::cerr << "conversion failed\n";
        return 1;
    }
    std::ofstream(argv[2]) << shader.sourceCode;
    std::cout << "shader_type=" << shader.shaderType << " bytes=" << shader.sourceCode.size()
              << '\n';
    return 0;
}
