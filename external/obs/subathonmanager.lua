-- SubathonManager OBS helper script
-- https://github.com/WolfwithSword/SubathonManager
--
-- Install: OBS -> Tools -> Scripts -> "+" -> select this file.

local obs = obslua

local HOTKEY_NAME = "subathonmanager_apply_tweaks"
local MARKER_KEY = "subathon_managed"
local REQUEST_KEY = "subathon_blend_request"
local STATES_KEY = "subathon_blend_states"
local SCRIPT_VERSION = "1.2.3"
local VERSION_HOTKEY_PREFIX = "subathonmanager_version_"

local hotkey_id = nil
local version_hotkey_id = nil

local collected = {}

local function method_to_name(method)
    if method == obs.OBS_BLEND_METHOD_SRGB_OFF then return "srgb_off" end
    return "default"
end

local function handle_item(scene_name, item)
    local src = obs.obs_sceneitem_get_source(item)
    if src == nil then return end
    if obs.obs_source_get_unversioned_id(src) ~= "browser_source" then return end

    local settings = obs.obs_source_get_settings(src)
    if settings == nil then return end

    if obs.obs_data_get_bool(settings, MARKER_KEY) then
        local name = obs.obs_source_get_name(src)
        local entry = collected[name]
        if entry == nil then
            entry = { states = {}, applied = false }
            collected[name] = entry
        end

        local item_id = obs.obs_sceneitem_get_id(item)

        local request = obs.obs_data_get_obj(settings, REQUEST_KEY)
        if request ~= nil then
            local method = obs.obs_data_get_string(request, "method")
            local target_scene = obs.obs_data_get_string(request, "scene")
            local target_item = obs.obs_data_get_int(request, "item")
            local targeted = (target_scene == nil or target_scene == "")
                or (target_scene == scene_name and target_item == item_id)

            if method ~= nil and method ~= "" and targeted then
                if method == "srgb_off" then
                    obs.obs_sceneitem_set_blending_method(item, obs.OBS_BLEND_METHOD_SRGB_OFF)
                elseif method == "default" then
                    obs.obs_sceneitem_set_blending_method(item, obs.OBS_BLEND_METHOD_DEFAULT)
                end
                entry.applied = true
            end
            obs.obs_data_release(request)
        end

        entry.states[scene_name .. "|" .. tostring(item_id)] =
            method_to_name(obs.obs_sceneitem_get_blending_method(item))
    end

    obs.obs_data_release(settings)
end

local function process_scene(scene, scene_name)
    if scene == nil then return end

    local items = obs.obs_scene_enum_items(scene)
    if items == nil then return end

    for _, item in ipairs(items) do
        handle_item(scene_name, item)
        if obs.obs_sceneitem_is_group(item) then
            local group_src = obs.obs_sceneitem_get_source(item)
            if group_src ~= nil then
                process_scene(obs.obs_sceneitem_group_get_scene(item),
                    obs.obs_source_get_name(group_src))
            end
        end
    end

    obs.sceneitem_list_release(items)
end

local function apply_tweaks()
    collected = {}

    local scenes = obs.obs_frontend_get_scenes()
    if scenes ~= nil then
        for _, scene_source in ipairs(scenes) do
            process_scene(obs.obs_scene_from_source(scene_source),
                obs.obs_source_get_name(scene_source))
        end
        obs.source_list_release(scenes)
    end

    for name, entry in pairs(collected) do
        local src = obs.obs_get_source_by_name(name)
        if src ~= nil then
            local update = obs.obs_data_create()

            local states = obs.obs_data_create()
            for key, value in pairs(entry.states) do
                obs.obs_data_set_string(states, key, value)
            end
            obs.obs_data_set_obj(update, STATES_KEY, states)
            obs.obs_data_release(states)

            if entry.applied then
                local cleared = obs.obs_data_create()
                obs.obs_data_set_string(cleared, "method", "")
                obs.obs_data_set_obj(update, REQUEST_KEY, cleared)
                obs.obs_data_release(cleared)
            end

            obs.obs_source_update(src, update)
            obs.obs_data_release(update)
            obs.obs_source_release(src)
        end
    end

    collected = {}
end

local function on_hotkey(pressed)
    if pressed then apply_tweaks() end
end

local function on_frontend_event(event)
    if event == obs.OBS_FRONTEND_EVENT_FINISHED_LOADING or
        event == obs.OBS_FRONTEND_EVENT_SCENE_COLLECTION_CHANGED then
        apply_tweaks()
    end
end

function script_description()
    return "SubathonManager helper (v" .. SCRIPT_VERSION .. ")\n\n" ..
        "Lets SubathonManager automate source tweaks it cannot over websocket.\n\n" ..
        "Leave this script loaded; SubathonManager detects it automatically."
end

function script_load(settings)
    hotkey_id = obs.obs_hotkey_register_frontend(HOTKEY_NAME,
        "SubathonManager: Apply overlay source tweaks", on_hotkey)
    version_hotkey_id = obs.obs_hotkey_register_frontend(
        VERSION_HOTKEY_PREFIX .. SCRIPT_VERSION,
        "SubathonManager: version marker (do not bind)",
        function(pressed) end)
    obs.obs_frontend_add_event_callback(on_frontend_event)
end
