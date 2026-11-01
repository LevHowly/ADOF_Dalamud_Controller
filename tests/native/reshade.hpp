#pragma once

#include <Windows.h>
#include <cstdint>
#include <cstring>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>

namespace reshade
{
    namespace api
    {
        using effect_technique = uint64_t;
        using effect_uniform_variable = uint64_t;
        using resource_view = uint64_t;
        using effect_texture_variable = uint64_t;
        using resource = uint64_t;
        enum class device_api { d3d11, other };
        enum class resource_type { texture_2d };
        enum class format { r32_float, r32_typeless, r16_float, r16_typeless, other };
        struct resource_view_desc { api::format format = api::format::r32_float; };
        enum class memory_heap { readback };
        enum class resource_usage { shader_resource, copy_source, copy_dest };
        enum class map_access { read_only };
        struct subresource_data { void *data = nullptr; };
        struct resource_desc
        {
            resource_type type = resource_type::texture_2d;
            struct
            {
                uint32_t width = 1, height = 1;
                api::format format = api::format::r32_float;
                uint16_t samples = 1;
            } texture;
            resource_desc() = default;
            resource_desc(uint32_t, uint32_t, uint16_t, uint16_t, api::format,
                          uint16_t, memory_heap, resource_usage) {}
        };
        struct device
        {
            device_api api = device_api::d3d11;
            float depth = 0.1f;
            float copiedDepth = 0;
            uint16_t halfDepth = 0;
            bool allocate = true, map = true, waited = false, flushed = false;
            unsigned allocations = 0, destructions = 0, unmaps = 0;
            resource_desc sourceDesc;
            resource_view_desc sourceViewDesc;
            device_api get_api() { return api; }
            resource get_resource_from_view(resource_view view) { return view; }
            resource_desc get_resource_desc(resource) { return sourceDesc; }
            resource_view_desc get_resource_view_desc(resource_view) { return sourceViewDesc; }
            bool create_resource(const resource_desc &, const void *, resource_usage, resource *out)
            {
                if (!allocate) return false;
                *out = 2;
                ++allocations;
                waited = flushed = false;
                return true;
            }
            void destroy_resource(resource)
            {
                if (!waited) throw std::runtime_error("Destroyed resource before GPU completion");
                ++destructions;
            }
            bool map_texture_region(resource, uint32_t, const void *, map_access, subresource_data *out)
            {
                if (!waited || !flushed) throw std::runtime_error("Readback before GPU completion");
                out->data = !map ? nullptr : sourceViewDesc.format == format::r16_float ?
                    static_cast<void *>(&halfDepth) : static_cast<void *>(&copiedDepth);
                return map;
            }
            void unmap_texture_region(resource, uint32_t) { ++unmaps; }
        };
        struct command_list
        {
            device *gpu = nullptr;
            void barrier(resource, resource_usage, resource_usage) {}
            void copy_texture_region(resource, uint32_t, const void *, resource, uint32_t, const void *)
            { gpu->copiedDepth = gpu->depth; }
        };
        struct command_queue
        {
            command_list commands;
            command_list *get_immediate_command_list() { return &commands; }
            void flush_immediate_command_list() { commands.gpu->flushed = true; }
            void wait_idle() { commands.gpu->waited = true; }
        };

        struct effect_runtime
        {
            struct technique
            {
                std::string file;
                std::string name;
                bool enabled;
            };
            std::vector<technique> techniques;
            std::map<std::string, effect_uniform_variable> uniforms;
            std::map<effect_uniform_variable, double> values;
            api::device gpu;
            command_queue queue;
            bool hasTexture = true;
            effect_runtime() { queue.commands.gpu = &gpu; }
            api::device *get_device() { return &gpu; }
            command_queue *get_command_queue() { return &queue; }
            effect_texture_variable find_texture_variable(const char *, const char *)
            { return hasTexture ? 1 : 0; }
            void get_texture_binding(effect_texture_variable, resource_view *view, resource_view *)
            { *view = 1; }

            effect_technique find_technique(const char *file, const char *name)
            {
                for (size_t i = 0; i < techniques.size(); ++i)
                    if (techniques[i].file == file && techniques[i].name == name)
                        return i + 1;
                return 0;
            }
            effect_uniform_variable find_uniform_variable(const char *file, const char *name)
            {
                const auto found = uniforms.find(std::string(file) + ":" + name);
                return found == uniforms.end() ? 0 : found->second;
            }
            template <typename F>
            void enumerate_techniques(const char *, F callback)
            {
                for (size_t i = 0; i < techniques.size(); ++i)
                    callback(this, i + 1);
            }
            template <size_t N>
            void get_technique_name(effect_technique handle, char (&name)[N])
            {
                strncpy_s(name, techniques.at(handle - 1).name.c_str(), N - 1);
            }
            bool get_technique_state(effect_technique handle) { return techniques.at(handle - 1).enabled; }
            void set_technique_state(effect_technique handle, bool enabled) { techniques.at(handle - 1).enabled = enabled; }
            void set_uniform_value_bool(effect_uniform_variable handle, const bool *value, size_t count)
            { if (count != 1) throw std::runtime_error("Expected a scalar write"); values[handle] = *value; }
            void get_uniform_value_bool(effect_uniform_variable handle, bool *value, size_t count)
            { if (count != 1) throw std::runtime_error("Expected a scalar read"); *value = values[handle] != 0; }
            void set_uniform_value_float(effect_uniform_variable handle, const float *value, size_t count)
            { if (count != 1) throw std::runtime_error("Expected a scalar write"); values[handle] = *value; }
            void set_uniform_value_int(effect_uniform_variable handle, const int32_t *value, size_t count)
            { if (count != 1) throw std::runtime_error("Expected a scalar write"); values[handle] = *value; }
        };
    }
    namespace log
    {
        enum class level { info, warning, error };
        inline void message(level, const char *) {}
    }
    enum class addon_event { reshade_present, reshade_reloaded_effects, reshade_render_technique, destroy_effect_runtime };
    inline bool register_addon(HMODULE, HMODULE) { return true; }
    inline void unregister_addon(HMODULE, HMODULE) {}
    template <addon_event Event, typename F> void register_event(F) {}
    template <addon_event Event, typename F> void unregister_event(F) {}
}
