# Portable scene API: no Win32, Direct3D, native window, or XInput libraries.
# main.cpp is included by unity_bridge.cpp, not compiled separately.
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
  set(IDAS3_PLUGIN_KIND STATIC)
else()
  set(IDAS3_PLUGIN_KIND SHARED)
endif()
set_source_files_properties(vendor/lz4/lz4.c PROPERTIES LANGUAGE CXX)
add_library(Idas3Unity ${IDAS3_PLUGIN_KIND}
  src/native_asset_storage.cpp
  vendor/lz4/lz4.c
  src/original_ending.cpp
  src/native_assets.cpp
  src/menu_font.cpp
  src/original_loading_screen.cpp
  src/original_vs_banner.cpp
  src/original_course_crows.cpp
  src/original_course_objects.cpp
  src/original_course_animation.cpp
  src/original_course_billboards.cpp
  src/original_headlight_projection.cpp
  src/original_aura.cpp
  src/original_name_entry_presentation.cpp
  src/original_tuning_course_presentation.cpp
  src/original_ranking_board.cpp
  src/original_ranking_playback.cpp
  src/original_attract_cards.cpp
  src/original_demo_overlays.cpp
  src/original_gasstand_attract.cpp
  src/original_tuning_ui.cpp
  src/original_tuning_presentation.cpp
  src/original_car_material_rebuild.cpp
  src/original_car_render_frame.cpp
  src/original_start_camera.cpp
  src/original_controls.cpp
  src/original_dynamics.cpp
  src/original_transmission.cpp
  src/original_vehicle.cpp
  src/original_initialization.cpp
  src/original_data.cpp
  src/original_math.cpp
  src/original_contact.cpp
  src/original_collision.cpp
  src/original_contact_completion.cpp
  src/original_host_input.cpp
  src/original_frame_state.cpp
  src/original_matrix.cpp
  src/original_road_contact.cpp
  src/original_session_initialization.cpp
  src/original_driving_session.cpp
  src/original_start_grid.cpp
  src/original_race_start.cpp
  src/original_race_rules.cpp
  src/original_race_path.cpp
  src/original_record_rules.cpp
  src/online_race_simulation.cpp
  src/online_race_timeline.cpp
  src/online_race_link.cpp
  src/original_name_entry.cpp
  src/original_course_fog.cpp
  src/original_course_lighting.cpp
  src/original_rival_light_request.cpp
  src/original_car_lighting.cpp
  src/original_car_light_gain.cpp
  src/original_driver_entry_route.cpp
  src/original_tuning_course_menu.cpp
  src/original_tuning_candidate.cpp
  src/original_card_check.cpp
  src/original_rival.cpp
  src/original_rival_initialization.cpp
  src/original_rival_motion.cpp
  src/original_body_contact.cpp
  src/original_battle_profile.cpp
  src/original_battle_metrics.cpp
  src/original_legend_progress.cpp
  src/original_legend_return.cpp
  src/original_rival_dialog.cpp
  src/original_rival_dialog_data.cpp
  src/original_rival_dialog_scene.cpp
  src/original_legend_choice.cpp
  src/original_legend_visit.cpp
  src/original_chase_camera.cpp
  src/original_engine_control.cpp
  src/original_engine_playback.cpp
  src/original_tire_audio.cpp
  src/original_sfx_sequence.cpp
  src/original_oneshot_playback.cpp
  src/original_stream_gain.cpp
  src/original_ics_audio.cpp
  src/original_ics_player.cpp
  src/original_music_voice.cpp
  src/original_music_band_limit.cpp
  src/original_music_bank.cpp
  src/original_selection_music.cpp
  src/original_music_control.cpp
  src/original_music_sequence.cpp
  src/original_music_playback.cpp
  src/original_audio_dsp.cpp
  src/original_audio_dsp_bank.cpp
  src/original_audio_dsp_control.cpp
  src/original_audio_dsp_runtime.cpp
  src/original_attract_audio.cpp
  src/original_demo_data.cpp
  src/original_ranking_attract.cpp
  src/original_bunta_setup.cpp
  src/original_bunta_results.cpp
  src/original_bunta_menu.cpp
  src/original_bunta_visit.cpp
  src/original_time_attack_visit.cpp
  src/original_time_attack_analysis.cpp
  src/original_time_attack_telemetry.cpp
  src/original_time_attack_stats.cpp
  src/original_time_attack_background.cpp
  src/original_frontend_transition.cpp
  src/original_battle_result_animation.cpp
  src/original_attract.cpp
  src/original_car_menu_transition.cpp
  src/original_transmission_menu_transition.cpp
  src/original_car_color_selection.cpp
  src/original_tuning.cpp
  src/original_tuning_child.cpp
  src/original_result_tuning_visit.cpp
  src/original_time_attack_points.cpp
  src/original_tuning_preview_state.cpp
  src/original_mode_menu_transition.cpp
  src/original_car_appearance_config.cpp
  src/original_car_visibility.cpp
  src/original_car_wheel_offsets.cpp
  src/original_car_assembly.cpp
  src/physics.cpp
  src/course.cpp
  src/race.cpp
  src/time_attack_ghost.cpp
  src/time_attack_records.cpp
  src/local_driver_profiles.cpp
  src/local_driver_setup.cpp
  src/local_save_slots.cpp
  src/renderer.cpp
  src/ui.cpp
  src/audio.cpp
  src/original_audio.cpp
  src/original_menu_audio.cpp
  src/frontend.cpp
  src/original_mode_menu.cpp
  src/original_choice_menu.cpp
  src/original_hud.cpp
  src/original_results.cpp
  src/car_shadow.cpp
  src/car_presentation.cpp
  src/course_scene_catalog.cpp
  src/original_number_plate.cpp
  src/original_car_body_position.cpp
  src/original_legend_menu.cpp
  src/original_battle_hud.cpp
  src/original_battle_names.cpp
  src/original_demo_presentation.cpp
  src/original_ranking_presentation.cpp
  src/original_tuning_preview.cpp
  src/unity_bridge.cpp
  src/unity_audio_output.cpp
  src/unity_scene_capture.cpp
  src/unity_ui_capture.cpp
)
target_include_directories(Idas3Unity PRIVATE src vendor/lz4)
target_compile_definitions(Idas3Unity PRIVATE IDAS3_UNITY_PLUGIN IDAS3_PORTABLE_SCENE)
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
  target_compile_definitions(Idas3Unity PRIVATE IDAS3_IOS)
endif()
target_compile_options(Idas3Unity PRIVATE -Wall -Wextra -ffp-contract=off -fno-fast-math)
set_target_properties(Idas3Unity PROPERTIES POSITION_INDEPENDENT_CODE ON)
if(NOT CMAKE_SYSTEM_NAME STREQUAL "iOS")
  enable_testing()
  add_executable(online_race_timeline_portable tests/online_race_timeline_tests.cpp)
  target_include_directories(online_race_timeline_portable PRIVATE src vendor/lz4)
  target_compile_definitions(online_race_timeline_portable PRIVATE IDAS3_PORTABLE_SCENE)
  target_link_libraries(online_race_timeline_portable PRIVATE Idas3Unity)
  add_test(NAME online_race_timeline COMMAND online_race_timeline_portable "${CMAKE_SOURCE_DIR}")
  add_executable(portable_scene_tests tests/portable_scene_tests.cpp)
  target_include_directories(portable_scene_tests PRIVATE src vendor/lz4)
  target_compile_definitions(portable_scene_tests PRIVATE IDAS3_PORTABLE_SCENE)
  target_link_libraries(portable_scene_tests PRIVATE Idas3Unity)
  add_test(NAME portable_scene COMMAND portable_scene_tests "${CMAKE_SOURCE_DIR}")
  add_executable(compressed_music_tests tests/compressed_music_tests.cpp)
  target_include_directories(compressed_music_tests PRIVATE src vendor/lz4)
  target_link_libraries(compressed_music_tests PRIVATE Idas3Unity)
  add_executable(race_music_catalog_portable tests/race_music_catalog_tests.cpp)
  target_include_directories(race_music_catalog_portable PRIVATE src vendor/lz4)
  target_compile_definitions(race_music_catalog_portable PRIVATE IDAS3_PORTABLE_SCENE)
  target_link_libraries(race_music_catalog_portable PRIVATE Idas3Unity)
  foreach(check IN ITEMS native_load_keepalive odawara_handling time_attack_records time_attack_ghost full_tune)
    add_executable(${check}_portable tests/${check}_tests.cpp)
    target_include_directories(${check}_portable PRIVATE src vendor/lz4)
    target_compile_definitions(${check}_portable PRIVATE IDAS3_PORTABLE_SCENE)
    target_link_libraries(${check}_portable PRIVATE Idas3Unity)
  endforeach()
  add_test(NAME native_load_keepalive COMMAND native_load_keepalive_portable)
  add_test(NAME odawara_handling COMMAND odawara_handling_portable "${CMAKE_SOURCE_DIR}" "${CMAKE_SOURCE_DIR}/../RuntimeAssets/ODAWARA")
  add_test(NAME time_attack_records COMMAND time_attack_records_portable)
  add_test(NAME time_attack_ghost COMMAND time_attack_ghost_portable)
  add_test(NAME full_tune COMMAND full_tune_portable "${CMAKE_SOURCE_DIR}")
  add_executable(chinese_arcade_tests tests/chinese_arcade_tests.cpp)
  target_include_directories(chinese_arcade_tests PRIVATE src vendor/lz4)
  target_compile_definitions(chinese_arcade_tests PRIVATE IDAS3_PORTABLE_SCENE)
  target_link_libraries(chinese_arcade_tests PRIVATE Idas3Unity)
  add_test(NAME chinese_arcade COMMAND chinese_arcade_tests "${CMAKE_SOURCE_DIR}" "${CMAKE_BINARY_DIR}/chinese-arcade-proof")
  add_executable(japanese_ui_portable tests/japanese_ui_tests.cpp)
  target_include_directories(japanese_ui_portable PRIVATE src vendor/lz4)
  target_compile_definitions(japanese_ui_portable PRIVATE IDAS3_PORTABLE_SCENE)
  target_link_libraries(japanese_ui_portable PRIVATE Idas3Unity)
  add_test(NAME japanese_ui COMMAND japanese_ui_portable "${CMAKE_SOURCE_DIR}" "${CMAKE_BINARY_DIR}/japanese-ui-proof")
  foreach(check IN ITEMS vs_banner original_time_attack_visit)
    add_executable(${check}_portable tests/${check}_tests.cpp)
    target_include_directories(${check}_portable PRIVATE src vendor/lz4)
    target_compile_definitions(${check}_portable PRIVATE IDAS3_PORTABLE_SCENE)
    target_link_libraries(${check}_portable PRIVATE Idas3Unity)
    foreach(language IN ITEMS 0 1)
      add_test(NAME ${check}_${language} COMMAND ${check}_portable "${CMAKE_SOURCE_DIR}"
        "${CMAKE_BINARY_DIR}/${check}-${language}-proof" ${language})
    endforeach()
  endforeach()
  add_test(NAME original_time_attack_visit_2 COMMAND original_time_attack_visit_portable "${CMAKE_SOURCE_DIR}"
    "${CMAKE_BINARY_DIR}/original_time_attack_visit-2-proof" 2)
endif()
