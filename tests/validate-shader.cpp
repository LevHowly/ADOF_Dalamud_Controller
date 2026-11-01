#include "effect_codegen.hpp"
#include "effect_parser.hpp"
#include "effect_preprocessor.hpp"
#include <filesystem>
#include <fstream>
#include <iostream>

int main(int argc, char **argv)
{
    if (argc != 4)
    {
        std::cerr << "Usage: validate-shader <GPose_ADOF.fx> <shader-include-directory> <output-directory>\n";
        return 1;
    }
    const std::filesystem::path output = argv[3];
    std::filesystem::create_directories(output);
    for (int chroma = 0; chroma <= 1; ++chroma)
    for (int vignette = 0; vignette <= 1; ++vignette)
    for (int width : {1920, 3840})
    {
        reshadefx::preprocessor pp;
        pp.add_include_path(argv[2]);
        pp.add_macro_definition("__RESHADE__", "60000");
        pp.add_macro_definition("__RESHADE_PERFORMANCE_MODE__", "0");
        pp.add_macro_definition("BUFFER_WIDTH", std::to_string(width));
        pp.add_macro_definition("BUFFER_HEIGHT", std::to_string(width * 9 / 16));
        pp.add_macro_definition("BUFFER_RCP_WIDTH", "(1.0 / BUFFER_WIDTH)");
        pp.add_macro_definition("BUFFER_RCP_HEIGHT", "(1.0 / BUFFER_HEIGHT)");
        pp.add_macro_definition("ADOF_CHROMATIC_ABERRATION_ENABLE", std::to_string(chroma));
        pp.add_macro_definition("ADOF_OPTICAL_VIGNETTE_ENABLE", std::to_string(vignette));
        if (!pp.append_file(argv[1]))
        {
            std::cerr << pp.errors();
            return 1;
        }
        std::unique_ptr<reshadefx::codegen> backend(reshadefx::create_codegen_hlsl(50, false, false));
        reshadefx::parser parser;
        if (!parser.parse(pp.output(), backend.get()))
        {
            std::cerr << pp.errors() << parser.errors();
            return 1;
        }
        if (backend->module().techniques.size() != 1 ||
            backend->module().techniques.front().name != "GPoseADOF")
        {
            std::cerr << "Unexpected GPose ADOF technique contract\n";
            return 1;
        }
        for (const auto &[entry, stage] : backend->module().entry_points)
        {
            std::string code, assembly, errors;
            if (!backend->assemble_code_for_entry_point(entry, code, assembly, errors))
            {
                std::cerr << errors;
                return 1;
            }
            const std::string variant = std::to_string(chroma) + std::to_string(vignette) +
                                        "_" + std::to_string(width) + "_" + entry;
            const std::string extension = stage == reshadefx::shader_type::vertex ? ".vert.hlsl" : ".frag.hlsl";
            std::ofstream(output / (variant + extension)) << code;
        }
    }
    std::cout << "PASS: ReShade FX frontend and HLSL generation for eight GPose ADOF variants.\n";
}
