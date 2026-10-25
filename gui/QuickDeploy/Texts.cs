using System;
using System.Collections.Generic;
using System.Globalization;
using Hd2Mod;

namespace QuickDeploy
{
    /// <summary>The window's own words, (English, Chinese); Chinese when Windows is.</summary>
    public static class Texts
    {
        public static bool Zh = Language();

        /// <summary>Whether the window shows the lines that explain things. Off for a player who
        /// knows the program (the user, 2026-10-24: "一看就知道，不需要看这些东西").</summary>
        public static bool Hints = LoadHints();

        static bool LoadHints()
        {
            try { return Json.Bool(Store.Load("gui.json"), "hints", true); }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.FormatException || e is System.UnauthorizedAccessException) { return true; }
        }

        /// <summary>The explaining lines shown or hidden, kept for the next start.</summary>
        public static void ShowHints(bool on)
        {
            Hints = on;
            try
            {
                var d = Store.Load("gui.json");
                d["hints"] = on;
                Store.Save("gui.json", d);
            }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.UnauthorizedAccessException) { }
        }

        /// <summary>Whether the list is shown with the mod that wins at its top. Only the way the
        /// list is drawn: the load order, what wins and the numbers stay as they are (the user,
        /// 2026-10-25, after HD2 Arsenal's "将第一个模组设置为最高优先级").</summary>
        public static bool TopWins = LoadTopWins();

        static bool LoadTopWins()
        {
            try { return Json.Bool(Store.Load("gui.json"), "top_wins", false); }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.FormatException || e is System.UnauthorizedAccessException) { return false; }
        }

        /// <summary>The list's direction chosen, kept for the next start.</summary>
        public static void ShowTopWins(bool on)
        {
            TopWins = on;
            try
            {
                var d = Store.Load("gui.json");
                d["top_wins"] = on;
                Store.Save("gui.json", d);
            }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.UnauthorizedAccessException) { }
        }

        /// <summary>The window's own setting (gui.json beside the library: "lang" zh or
        /// en; the user, 2026-10-24: not the settings window's), else Windows's language.</summary>
        static bool Language()
        {
            try
            {
                var s = Json.Str(Store.Load("gui.json"), "lang");
                if (s == "zh" || s == "en") return s == "zh";
            }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.FormatException || e is System.UnauthorizedAccessException) { }
            return CultureInfo.CurrentUICulture.Name.StartsWith("zh");
        }

        /// <summary>The language chosen in the window, kept for the next start.</summary>
        public static void Choose(bool zh)
        {
            Zh = zh;
            try
            {
                var d = Store.Load("gui.json");
                d["lang"] = zh ? "zh" : "en";
                Store.Save("gui.json", d);
            }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.FormatException || e is System.UnauthorizedAccessException) { }
        }

        static readonly Dictionary<string, (string En, string Zh)> T = new Dictionary<string, (string, string)>
        {
            ["window"] = ("HD2 Mod Quick Deploy", "HD2 Mod Quick Deploy"),
            ["title"] = ("HD2 MOD QUICK DEPLOY", "HD2 MOD QUICK DEPLOY"),
            ["summary"] = ("{0} mods · {1} on · {2} to deploy", "{0} 个 MOD · {1} 个启用 · {2} 项待部署"),
            ["stat_links"] = ("Hard-linked", "硬链接部署"),
            ["stat_links_v"] = ("{0} files · {1}", "{0} 个文件 · {1}"),
            ["stat_links_v_1"] = ("1 file · {1}", "1 个文件 · {1}"),
            ["stat_saved"] = ("Writes saved", "累计节省写盘"),
            // the filters
            ["library"] = ("LIBRARY", "MOD 库"),
            ["f_all"] = ("All", "全部"),
            ["f_pending"] = ("To deploy", "待部署"),
            ["f_options"] = ("With options", "有可选内容"),
            ["f_off"] = ("Off", "已停用"),
            ["f_conflict"] = ("Conflicts", "冲突"),
            ["f_broken"] = ("Cannot be read", "读取出错"),
            ["t_all"] = ("ALL MODS", "全部 MOD"),
            // the deploy target
            ["target"] = ("DEPLOY TARGET", "部署目标"),
            ["target_link"] = ("Same drive · hard links", "同一分区 · 可硬链接"),
            ["target_copy"] = ("Another drive · copies", "不同分区 · 将复制"),
            ["target_mixed"] = ("Some mods on another drive", "部分 MOD 在其他分区"),
            ["target_lib"] = ("Mod library", "mod 库"),
            ["target_game"] = ("Game folder", "游戏目录"),
            ["target_note"] = ("Mods on another drive than the game are copied instead, and shown here.",
                               "与游戏不在同一分区的 mod 将改为复制，并在此处提示。"),
            // two lines of its own: the card is narrow and "103 MB" must not break in half
            ["copies"] = ("{0} files are copies\n{1}", "{0} 个文件是复制件\n{1}"),
            ["copies_1"] = ("1 file is a copy\n{1}", "1 个文件是复制件\n{1}"),
            ["copies_note"] = ("Already right, but each takes its own space. As hard links they take none; nothing is written.",
                               "内容正确，但各自占用空间。改为硬链接后不再额外占用空间，也不写入硬盘。"),
            ["relink"] = ("Make hard links", "改为硬链接"),
            ["no_game"] = ("No game folder yet: choose it under Deploy target", "尚未设置游戏目录：请在左侧“部署目标”中选择"),
            ["game_pick"] = ("Choose the game folder…", "选择游戏目录…"),
            ["game_change"] = ("Change…", "更改…"),
            ["game_pick_title"] = ("Helldivers 2's folder (…\\steamapps\\common\\Helldivers 2)", "Helldivers 2 的游戏目录（…\\steamapps\\common\\Helldivers 2）"),
            ["game_ask"] = ("Helldivers 2's folder was not found. Choose it next: usually …\\steamapps\\common\\Helldivers 2 in your Steam library.",
                            "未找到 Helldivers 2 的游戏目录。请选择该文件夹，通常位于 Steam 库的 …\\steamapps\\common\\Helldivers 2。"),
            ["not_game"] = ("{0} is not Helldivers 2's folder: it has no data folder. Choose again.",
                            "{0} 不是 Helldivers 2 的游戏目录（其中没有 data 文件夹），请重新选择。"),
            ["arsenal_ask"] = ("HD2 Arsenal has a list of {0} mods ({1} on). Import its list, load order, on/off and options? HD2 Arsenal's files are only read.\n\nAfterwards deploy with this window only: a deploy from HD2 Arsenal would undo it.",
                               "检测到 HD2 Arsenal 的 mod 列表：{0} 个 mod（{1} 个启用）。是否导入其列表、加载顺序、开关和可选内容？仅读取，不会修改 HD2 Arsenal 的文件。\n\n导入后请仅使用本程序部署，再用 HD2 Arsenal 部署会覆盖本程序的部署。"),
            ["arsenal_import"] = ("Import", "导入"),
            ["add_dupe"] = ("A mod named “{0}” is already in the library. To use this as its new version, right-click that mod and choose New version.",
                            "库中已有名为「{0}」的 mod。如需替换为此新版本，请右键该 mod，选择「换新版本」。"),
            // the list
            ["add"] = ("Add mod…", "添加 mod…"),
            ["add_zip"] = ("From a zip…", "从 zip…"),
            ["add_folder"] = ("From a folder…", "从文件夹…"),
            // the system's windows for choosing a file or a folder
            ["pick_all"] = ("All files", "所有文件"),
            ["pick_zip_title"] = ("Choose a mod archive", "选择 mod 压缩包"),
            ["pick_zip_kind"] = ("Mod archives (zip, 7z, rar)", "mod 压缩包（zip、7z、rar）"),
            ["pick_mod_title"] = ("Choose the mod's folder", "选择 mod 所在的文件夹"),
            ["pick_lib_title"] = ("Choose a folder to keep mods in", "选择存放 mod 的文件夹"),
            ["pick_move_title"] = ("Choose where to move the mods", "选择要把 mod 移动到的文件夹"),
            ["jump_top"] = ("To the top", "回到顶部"),
            ["jump_bottom"] = ("To the end", "跳到底部"),
            ["refresh"] = ("Refresh", "刷新"),
            ["opts_some"] = ("Options · {0} / {1} chosen", "可选内容 · 已选 {0} / {1}"),
            ["opts_none"] = ("No options", "无可选内容"),
            ["row_clash"] = ("{0} resource conflicts", "{0} 处资源冲突"),
            ["row_clash_1"] = ("1 resource conflict", "1 处资源冲突"),
            ["row_same"] = ("{0} shared, identical", "{0} 处文件与其他 mod 相同"),
            ["row_same_1"] = ("1 shared, identical", "1 处文件与其他 mod 相同"),
            ["n_files"] = ("{0} files", "{0} 个文件"),
            ["n_files_1"] = ("1 file", "1 个文件"),
            ["st_link"] = ("hard link", "硬链接"),
            ["st_copy"] = ("copy", "复制"),
            ["st_partial"] = ("partly linked", "部分链接"),
            ["st_pending"] = ("to deploy", "待部署"),
            ["st_off"] = ("not deployed", "未部署"),
            ["st_broken"] = ("cannot be read", "读取出错"),
            ["st_keep"] = ("nothing to deploy", "无文件"),
            ["no_options"] = ("This mod has no options: on, it loads whole.", "此 mod 没有可选内容，启用后全部加载。"),
            ["rule_one"] = ("Pick one · Ctrl+click for more", "单选 · Ctrl+点击可多选"),
            ["rule_any"] = ("Any · {0} / {1} chosen", "可多选 · 已选 {0} / {1}"),
            ["extras"] = ("Parts", "附加内容"),
            ["card_meta"] = ("{0} files · {1}", "{0} 个文件 · {1}"),
            ["card_meta_1"] = ("1 file · {1}", "1 个文件 · {1}"),
            ["card_vanilla"] = ("no files", "无文件"),
            ["opt_foot"] = ("{0} chosen · files deployed: {1} ({2})", "已选 {0} 项 · 部署 {1} 个文件（{2}）"),
            ["list_hint"] = ("Open a mod with the arrow to choose what it loads. Drag a row to change the load order: lower wins. Drop a zip archive or a mod folder on the list to install it. Double-click a mod to give it your own name; Ctrl or Shift takes several at once, and the right button acts on all of them.",
                             "点击左侧箭头展开 mod，选择要加载的内容。拖动行可调整加载顺序，越靠下优先级越高。将 zip 压缩包或 mod 文件夹拖到列表上即可安装。双击 mod 可自定义名称；按住 Ctrl 或 Shift 可多选，右键可对选中的 mod 一起操作。"),
            ["list_hint_top"] = ("Open a mod with the arrow to choose what it loads. Drag a row to change the load order: higher wins. Drop a zip archive or a mod folder on the list to install it. Double-click a mod to give it your own name; Ctrl or Shift takes several at once, and the right button acts on all of them.",
                                 "点击左侧箭头展开 mod，选择要加载的内容。拖动行可调整加载顺序，越靠上优先级越高。将 zip 压缩包或 mod 文件夹拖到列表上即可安装。双击 mod 可自定义名称；按住 Ctrl 或 Shift 可多选，右键可对选中的 mod 一起操作。"),
            // dropping mods onto the list from Explorer
            ["drop_here"] = ("Drop to install", "松开以安装"),
            ["drop_no"] = ("Cannot be installed", "无法安装"),
            ["drop_what"] = ("{0} will be added to the library", "将有 {0} 项加入 mod 库"),
            ["drop_none"] = ("Nothing here can be installed: drop a zip archive (.zip, .7z, .rar) or a mod folder.",
                             "无法安装拖入的内容，请拖入 zip 压缩包（.zip、.7z、.rar）或 mod 文件夹。"),
            ["drop_done"] = ("{0} mods added", "已添加 {0} 个 mod"),
            // the details
            ["manifest"] = ("MANIFEST // DESCRIPTION", "MANIFEST // 作者说明"),
            ["no_desc"] = ("The mod has no description.", "此 mod 没有说明。"),
            ["more"] = ("Show all", "展开全文"),
            ["less"] = ("Show less", "收起"),
            ["d_folder"] = ("Folder", "文件夹"),
            ["d_source"] = ("Added from", "来源"),
            ["d_opts"] = ("Chosen", "已选内容"),
            ["d_files"] = ("Patch files", "patch 文件"),
            ["d_order"] = ("Load order", "加载顺序"),
            ["d_order_v"] = ("{0} of {1}", "第 {0} / {1}"),
            ["changes"] = ("TO DEPLOY", "待部署的变更"),
            ["by_size"] = ("BY SIZE", "按体积"),
            ["c_keep"] = ("Unchanged · kept", "未变 · 保留"),
            ["c_move"] = ("Moved · renamed", "移动 · 仅重命名"),
            ["c_write"] = ("New", "新增"),
            ["c_delete"] = ("Deleted", "删除"),
            ["tile_write"] = ("Actual writing", "实际写盘"),
            ["tile_write_n"] = ("hard links take none", "硬链接不写盘"),
            ["tile_write_c"] = ("copied: another drive", "另一分区：复制"),
            ["tile_reuse"] = ("Reused", "复用"),
            ["tile_reuse_n"] = ("{0} files left or renamed", "{0} 个文件保留或仅重命名"),
            ["tile_reuse_n_1"] = ("1 file left or renamed", "1 个文件保留或仅重命名"),
            ["files_changed"] = ("CHANGED FILES", "变更的文件"),
            ["tag_write"] = ("NEW", "增"),
            ["tag_move"] = ("MOV", "移"),
            ["tag_del"] = ("DEL", "删"),
            ["moved_from"] = ("from {0}", "原 {0}"),
            ["in_sync"] = ("Deployed · matches the game folder", "已部署 · 与游戏目录一致"),
            ["in_sync_n"] = ("{0} files in the game, nothing to change.", "游戏目录中有它的 {0} 个文件，无需更改。"),
            ["in_sync_n_1"] = ("1 file in the game, nothing to change.", "游戏目录中有它的 1 个文件，无需更改。"),
            ["is_off"] = ("Off", "已停用"),
            ["is_off_n"] = ("Its files are not in the game folder; the library keeps them.", "游戏目录中没有它的文件，mod 库中的文件保留。"),
            ["broken"] = ("Cannot be read", "无法读取"),
            ["clash"] = ("{0} resources also in {1}", "与 {1} 有 {0} 处资源冲突"),
            ["clash_1"] = ("1 resource also in {1}", "与 {1} 有 1 处资源冲突"),
            ["clash_win"] = ("By the load order, {0}'s version is used.", "按加载顺序，{0} 的版本生效。"),
            ["clash_more"] = ("and {0} more", "另有 {0} 处"),
            ["drag_n"] = ("{0} mods", "{0} 个 MOD"),
            ["clash_no_names"] = ("Resource names come from HD2SDK's name list. HD2SDK is not installed here, so only the type and id are shown.",
                                  "资源名称取自 HD2SDK 的名称表。本机未安装 HD2SDK，因此只显示类型与编号。"),
            // resource types as the conflicts name them; any other type is shown by its engine name
            ["rt_unit"] = ("Model", "模型"),
            ["rt_texture"] = ("Texture", "贴图"),
            ["rt_material"] = ("Material", "材质"),
            ["rt_animation"] = ("Animation", "动画"),
            ["rt_bones"] = ("Bones", "骨骼"),
            ["rt_state_machine"] = ("Animation states", "动画状态机"),
            ["rt_particles"] = ("Particle effect", "粒子特效"),
            ["rt_wwise_bank"] = ("Sound bank", "音频库"),
            ["rt_wwise_stream"] = ("Sound", "音频"),
            ["rt_wwise_dep"] = ("Sound link", "音频关联"),
            ["rt_geometry_group"] = ("Geometry", "几何体"),
            ["rt_physics"] = ("Physics", "物理"),
            ["rt_lua"] = ("Script", "脚本"),
            ["rt_strings"] = ("Text", "文本"),
            ["rt_font"] = ("Font", "字体"),
            ["rt_package"] = ("Package", "资源包"),
            ["rt_prefab"] = ("Prefab", "预制件"),
            ["rt_entity"] = ("Entity", "实体"),
            ["rt_level"] = ("Level", "关卡"),
            ["rt_cloth"] = ("Cloth", "布料"),
            ["rt_shading_environment"] = ("Lighting", "光照环境"),
            ["rt_shading_environment_mapping"] = ("Lighting mapping", "光照环境映射"),
            // the same resource in two mods, byte for byte: shared, not fought over
            ["same"] = ("{0} resources are the same file as in {1}", "与 {1} 有 {0} 处文件完全相同"),
            ["same_1"] = ("1 resource is the same file as in {1}", "与 {1} 有 1 处文件完全相同"),
            ["same_tip"] = ("Both mods ship these resources byte for byte alike, so whichever loads last changes nothing.",
                            "两个 mod 携带的这些资源内容完全相同，无论哪个后加载，结果均相同。"),
            ["deploy_info"] = ("DEPLOY", "部署"),
            ["di_mode"] = ("How", "方式"),
            ["di_files"] = ("Files in the game", "已部署文件"),
            ["di_lib"] = ("In the library", "库中占用"),
            ["di_extra"] = ("Extra space in the game", "游戏目录额外占用"),
            ["mode_link"] = ("Hard link (same drive)", "硬链接（同一分区）"),
            ["mode_copy"] = ("Copy", "复制"),
            ["mode_partial"] = ("Partly hard-linked", "部分硬链接"),
            ["mode_off"] = ("Not deployed", "未部署"),
            ["mode_pending"] = ("Waiting for a deploy", "等待部署"),
            // the footer
            ["deploy"] = ("Deploy", "部署"),
            ["deploy_n"] = ("Deploy · {0} changes", "部署 · {0} 项变更"),
            ["deploy_n_1"] = ("Deploy · 1 change", "部署 · 1 项变更"),
            ["purge"] = ("Restore vanilla", "恢复原版"),
            ["tip_no_purge"] = ("In this mod manager, installing a mod never needs a purge first.\n\nInstall the mod, then click Deploy. Only what has changed is handled, usually in under half a second.\n\nRestore vanilla only to play the game without mods; it is at the bottom of the card on the left.\n\nThe explaining lines in the window can be turned off in Settings.",
                                "在本 MOD 管理器中安装 MOD，永远不需要先点击“清空”。\n\n安装后点击“部署”即可，只处理有变化的部分，通常不到半秒完成。\n\n“恢复原版”仅用于不加载任何 MOD 的情况，位于左侧卡片底部。\n\n窗口中的说明文字可在“设置”中关闭。"),
            ["purge_long"] = ("Restore vanilla", "恢复原版"),
            ["purge_note"] = ("A deploy never needs a purge first. Restore vanilla only to play the game without mods.", "部署前无需清空。仅在需要不加载任何 mod 时使用。"),
            ["synced"] = ("Deployed · the game folder matches the library", "部署完成 · 游戏目录与 mod 库同步"),
            ["pending"] = ("To deploy: {0} new, {1} renamed, {2} deleted · writing {3}",
                           "部署将新增 {0}、移动 {1}、删除 {2} 个文件 · 实际写盘 {3}"),
            ["lost"] = ("The mod library cannot be read. What was on show has been taken away: it is not the library as it is now. Trying again…",
                        "读不到 MOD 库。原先显示的内容已清除，那不是当前的库。正在重试…"),
            ["reading"] = ("Reading…", "读取中…"),
            ["working"] = ("Working…", "处理中…"),
            ["launch"] = ("Launch game", "启动游戏"),
            ["remove_btn"] = ("Remove mod", "移除 mod"),
            ["delete_btn"] = ("Delete", "删除"),
            ["delete_disk"] = ("Delete from the disk…", "从硬盘删除…"),
            ["delete_ask"] = ("Delete “{0}” from the disk for good?\n\nIts folder is deleted, not moved to the Recycle Bin:\n{1}\n\nIt is also taken out of the library and out of the game (a deploy runs now). The zip it was added from is kept.",
                              "从硬盘永久删除「{0}」？\n\n其文件夹将被直接删除，不进入回收站，无法恢复：\n{1}\n\n同时从 mod 库和游戏目录中移除（立即部署一次）。添加时使用的 zip 不会删除。"),
            ["deleted_mod"] = ("{0} deleted from the disk: {1} freed", "已从硬盘删除 {0}，释放 {1}"),
            ["del_protected"] = ("{0} is not a mod's own folder (a drive, the game or the settings folder), so it is not deleted. Nothing was changed.",
                                 "{0} 不是 mod 自身的文件夹（为盘符、游戏目录或设置文件夹），因此未删除，也未做任何更改。"),
            ["del_shared"] = ("Other mods in the library use the folder {0}, so it is not deleted. Nothing was changed.",
                              "库中其他 mod 也在使用文件夹 {0}，因此未删除，也未做任何更改。"),
            ["delete_failed"] = ("The mod is out of the library and the game, but its folder could not be deleted (is a file in it open?):\n{0}",
                                 "mod 已从库和游戏目录中移除，但文件夹未能删除（可能有文件被占用）：\n{0}"),
            ["f_lua"] = ("Lua mods", "Lua mod"),
            // the three pages and the search
            ["page_deploy"] = ("Deploy", "部署"),
            ["page_download"] = ("Download", "下载"),
            ["page_storage"] = ("Storage", "存储"),
            ["page_tags"] = ("Labels", "标签"),
            // the settings window
            ["settings"] = ("Settings", "设置"),
            ["set_lang"] = ("LANGUAGE", "语言"),
            ["set_profiles"] = ("PROFILES", "配置文件"),
            ["set_profiles_on"] = ("Use profiles", "启用配置文件"),
            ["set_profiles_note"] = ("On: the mod library holds every mod, and each profile some of them, with its own load order, on/off and options. The profile is chosen at the list's title. Off: one list; the profiles are kept.",
                                     "开启后，mod 库保存全部 mod，每个配置文件包含其中一部分，并有各自的加载顺序、开关与可选内容；在列表标题处切换配置文件。关闭后恢复为单一列表，配置文件会保留。"),
            ["pf_default"] = ("Default", "默认"),
            ["im_profiles_ask"] = ("These managers keep several profiles:\n{0}\n\nBring every profile in as a profile of its own (each with its mods, load order, on/off and options), or only the one each uses?",
                                   "以下管理器有多个配置：\n{0}\n\n将全部配置分别导入为配置文件（各自保留 mod、加载顺序、开关与可选内容），还是只导入各自当前使用的配置？"),
            ["im_profiles_all"] = ("Every profile", "导入全部配置"),
            ["im_profiles_one"] = ("Only the one in use", "只导入当前配置"),
            ["im_profiles_line"] = ("{0}: {1}", "{0}：{1}"),
            ["pf_library"] = ("Mod library", "MOD 库"),
            ["pf_library_n"] = ("Mod library (all {0})", "MOD 库（全部 {0} 个）"),
            ["pf_new"] = ("New profile…", "新建配置文件…"),
            ["pf_new_ask"] = ("The new profile's name:", "新配置文件的名称："),
            ["pf_new_how"] = ("Start “{0}” from the profile in use, or empty?", "新配置文件“{0}”从当前配置复制，还是从空白开始？"),
            ["pf_copy"] = ("Copy the one in use", "复制当前配置"),
            ["pf_empty"] = ("Empty", "空白"),
            ["pf_rename"] = ("Rename “{0}”…", "重命名“{0}”…"),
            ["pf_rename_ask"] = ("New name:", "新名称："),
            ["pf_delete"] = ("Delete profiles…", "删除配置文件…"),
            ["pf_delete_ask"] = ("Delete these profiles? Their mods stay in the library.", "删除以下配置文件？其中的 mod 仍保留在 mod 库中。"),
            ["pf_add"] = ("Put into “{0}”", "加入“{0}”"),
            ["pf_add_n"] = ("Put these {1} into “{0}”", "将选中的 {1} 个加入“{0}”"),
            ["pf_remove"] = ("Take out of “{0}”", "移出“{0}”"),
            ["pf_remove_n"] = ("Take these {1} out of “{0}”", "将选中的 {1} 个移出“{0}”"),
            ["pf_outside"] = ("not in this profile", "不在当前配置中"),
            ["set_order"] = ("DISPLAY ORDER", "显示顺序"),
            ["set_order_bottom"] = ("Bottom wins", "下面优先"),
            ["set_order_top"] = ("Top wins", "上面优先"),
            ["set_order_note"] = ("Changes only which way the list is shown. What wins in the game stays the same, with no new deploy.",
                                  "仅改变列表的显示方向；游戏中的覆盖关系不变，无需重新部署。"),
            ["set_hints"] = ("EXPLAINING LINES", "提示信息"),
            ["set_hints_on"] = ("Show the lines that explain things", "显示说明文字"),
            ["set_hints_note"] = ("Off: the window shows only what it does and what it found. Everything still works the same.",
                                  "关闭后仅显示操作与结果，不再显示说明文字；功能不受影响。"),
            ["set_keys"] = ("HOW TO WORK THE LIST", "操作说明"),
            ["set_key_rename"] = ("Double-click: change a mod's shown name", "双击：更改 MOD 的显示名称"),
            ["set_key_many"] = ("Ctrl or Shift: select several mods", "Ctrl 或 Shift：多选 MOD"),
            ["set_about"] = ("ABOUT", "关于"),
            ["set_update"] = ("UPDATES", "更新"),
            ["build_official"] = ("official build", "正式版"),
            ["build_dev"] = ("development build", "开发版"),
            ["self_none"] = ("Not checked yet.", "尚未检查。"),
            ["self_current"] = ("Up to date. Last checked {0}.", "已是最新版本。上次检查：{0}"),
            ["self_no_release"] = ("No release on GitHub yet. Last checked {0}.", "GitHub 上尚无发布版本。上次检查：{0}"),
            ["self_new"] = ("Version {0} is out.", "有新版本：{0}"),
            ["self_pre"] = (" (pre-release)", "（预发布）"),
            ["self_failed"] = ("The check did not go through: {0}.", "检查失败：{0}。"),
            ["self_checking"] = ("Checking…", "正在检查…"),
            ["self_check"] = ("Check for updates", "检查更新"),
            ["self_open"] = ("Open download page", "打开下载页"),
            ["self_auto"] = ("Check for updates automatically", "自动检查更新"),
            ["self_auto_note_official"] = ("At most once a day. Only the list of releases on GitHub is read; nothing is downloaded. Only full releases are offered.",
                                           "每天最多检查一次，仅读取 GitHub 上的发布列表，不会自动下载。只提示正式版本。"),
            ["self_auto_note_dev"] = ("At most once a day. Only the list of releases on GitHub is read; nothing is downloaded. Pre-releases are offered too.",
                                      "每天最多检查一次，仅读取 GitHub 上的发布列表，不会自动下载。预发布版本也会提示。"),
            ["self_tip"] = ("Version {0} is out: see Settings", "有新版本 {0}，详见设置"),
            ["set_no_nexus"] = ("(no Nexus Mods support in this build)", "（此版本不含 Nexus Mods 功能）"),
            ["author_name"] = ("Ko-fi.com/vegansquid", "没盖De乌龟"),
            ["author_url"] = ("https://ko-fi.com/vegansquid", "https://space.bilibili.com/1838498"),
            ["project_name"] = ("github.com/pat-pat-patrick/hd2-mod-cli", "github.com/pat-pat-patrick/hd2-mod-cli"),
            ["project_url"] = ("https://github.com/pat-pat-patrick/hd2-mod-cli", "https://github.com/pat-pat-patrick/hd2-mod-cli"),
            ["close"] = ("Close", "关闭"),
            // the filter beside the search box
            ["filter"] = ("Filter", "筛选"),
            ["filter_n"] = ("Filter · {0}", "筛选 · {0}"),
            ["filter_tags"] = ("Labels", "标签"),
            ["filter_tags_n"] = ("Labels · {0}", "标签 · {0}"),
            ["filter_no_tags"] = ("No labels yet", "尚未创建标签"),
            // two lines, so the menu is no wider than its labels (the user, 2026-10-25)
            ["filter_tags_alt"] = ("Alt+click a label to hide\nthe mods that carry it", "按住 Alt 键点击标签，\n可隐藏带有该标签的 MOD"),
            ["filter_nexus"] = ("Nexus Mods", "Nexus Mods"),
            ["filter_github"] = ("GitHub", "GitHub"),
            ["filter_no_from"] = ("Source not recorded", "未记录来源"),
            ["filter_lua"] = ("Lua mods", "Lua mod"),
            ["filter_loader"] = ("Loaders", "加载器"),
            ["filter_asset"] = ("Files only, no scripts", "纯资源 mod"),
            // the storage page
            ["st_title"] = ("MOD FOLDERS", "MOD 库文件夹"),
            ["st_saved_title"] = ("WHAT HARD LINKS SAVE", "硬链接带来的节省"),
            ["st_saved_note"] = ("Hard-linked files take no space of their own in the game folder, and a deploy that only links writes nothing to the disk. Writes saved counts every deploy made so far.",
                                 "以硬链接部署的文件不会在游戏目录中额外占用空间，只建立链接的部署也几乎不写入硬盘。“累计节省写盘”为历次部署的总和。"),
            ["st_intro"] = ("The folders that hold mods, as Steam's library folders hold games. New mods (added zips, downloads) go to the default folder. Mods in a folder on the game's drive are deployed as hard links: no extra space, almost no writing.",
                            "存放 mod 的文件夹，作用类似 Steam 的游戏库文件夹：可以有多个，新 mod（添加的 zip、下载）放入默认文件夹。与游戏位于同一分区的文件夹中的 mod 以硬链接部署，不占用额外空间，几乎不写入硬盘。"),
            ["sort_by"] = ("Sort by", "排序"),
            ["sort_size"] = ("Size", "大小"),
            ["sort_name"] = ("Name", "名称"),
            ["sort_order"] = ("Load order", "加载顺序"),
            ["st_add_folder"] = ("Add library folder…", "添加库文件夹…"),
            ["lf_ask"] = ("This folder holds mods that are not in the library yet. Bring them in?", "此文件夹中有尚未加入 mod 库的 mod。是否加入？"),
            ["lf_loose"] = ("{0} other mod folders: added where they are, at the bottom", "其他 mod 文件夹 {0} 个：原地加入，位于列表底部"),
            ["st_change_path"] = ("Change path…", "更改路径…"),
            ["st_path_title"] = ("Where is this folder now?", "选择此文件夹现在的位置"),
            ["st_path_done"] = ("{0} mods now point to {1}. Deploy once to bring the game folder up to date.", "{0} 个 mod 已改为指向 {1}。部署一次即可更新游戏目录。"),
            ["st_path_missing"] = ("{0} mods were not found there and still point to the old place.", "{0} 个 mod 在新位置未找到，仍指向原位置。"),
            ["st_game"] = ("GAME FOLDER", "游戏目录"),
            ["st_game_ok"] = ("Helldivers 2 is here", "已识别为 Helldivers 2 游戏目录"),
            ["st_game_gone"] = ("Helldivers 2 is not there any more", "此位置已找不到 Helldivers 2"),
            ["st_game_none"] = ("No game folder yet", "尚未设置游戏目录"),
            ["st_game_change"] = ("Change…", "更改…"),
            ["st_game_find"] = ("Find automatically", "自动识别"),
            ["st_game_same"] = ("Found the folder already in use:\n{0}", "识别结果与当前目录相同：\n{0}"),
            ["st_game_none_found"] = ("Helldivers 2 was not found in the Steam libraries on this computer. Choose its folder with Change….", "未在本机的 Steam 库中找到 Helldivers 2，请点击“更改…”手动选择。"),
            ["st_game_switch"] = ("Deploy to this folder from now on?\n{0}\n\nThe mods already deployed in {1} stay there. To take them out, restore vanilla before changing.",
                                  "今后部署到此目录？\n{0}\n\n已部署在 {1} 中的 mod 文件会保留。如需移除，请在更改前点击“恢复原版”。"),
            ["st_game_switch_go"] = ("Use this folder", "使用此目录"),
            // the labels page
            ["tag_title"] = ("LABELS", "标签"),
            ["tag_intro"] = ("Labels of your own for sorting mods: a set you play with, mods for one occasion, mods to try. A mod may carry several. The Labels filter beside the search box narrows the list to one.",
                             "为 mod 自行设置的标签，可按任意方式归类：常用组合、特定场合、待测试等。一个 mod 可同时属于多个标签。列表上方搜索框左侧的“标签”可按标签筛选。"),
            ["tag_none"] = ("No labels yet. Make one, then choose the mods that carry it.", "尚无标签。新建后，在下方选择属于该标签的 mod。"),
            ["tag_new"] = ("New label", "新建标签"),
            ["tag_new_hint"] = ("A name for a new label", "新标签的名称"),
            ["tag_new_dlg"] = ("New label…", "新建标签…"),
            ["tag_new_ask"] = ("A name for the new label.", "新标签的名称。"),
            ["tag_new_save"] = ("Make it", "新建"),
            ["tag_rename"] = ("Rename…", "重命名…"),
            ["tag_rename_ask"] = ("A new name for this label. Every mod that carries it follows.", "该标签的新名称，使用该标签的 mod 将同步更新。"),
            ["tag_rename_save"] = ("Rename", "重命名"),
            ["tag_delete"] = ("Delete", "删除"),
            ["tag_delete_ask"] = ("Delete the label {0}? It comes off every mod that carries it; the mods themselves are not touched.",
                                  "要删除标签“{0}”吗？它会从所有 mod 上移除，mod 本身不受影响。"),
            ["tag_show"] = ("Show in the list", "在列表中筛选"),
            ["tag_summary"] = ("{0} mods · {1} of mine · {2} from manifests", "{0} 个 MOD · {1} 个我的标签 · {2} 个来自 manifest"),
            ["tag_mine"] = ("MY LABELS", "我的标签"),
            ["tag_auto"] = ("FROM THE MANIFEST", "来自 MANIFEST"),
            ["tag_no_match"] = ("No label matches", "无匹配的标签"),
            ["tag_hidden"] = ("hidden", "已隐藏"),
            ["tag_kind_mine"] = ("MY LABEL", "我的标签"),
            ["tag_kind_auto"] = ("FROM THE MANIFEST · READ ONLY", "来自 MANIFEST · 只读"),
            ["tag_auto_note"] = ("This label is written in the mod's own manifest: its name and what carries it are the author's. You can hide it, so it stays out of the filter.",
                                 "该标签写在 mod 自身的 manifest 中，名称与归属由作者决定，无法修改。可将其隐藏，使其不出现在筛选中。"),
            ["tag_hide"] = ("Hide from the filter", "在筛选中隐藏"),
            ["tag_unhide"] = ("Show in the filter", "取消隐藏"),
            ["tag_note"] = ("Note", "备注"),
            ["tag_on_n"] = ("MODS WITH THIS LABEL · {0}", "带这个标签的 MOD · {0} 个"),
            ["tag_only"] = ("Enable all", "批量启用"),
            ["tag_only_tip"] = ("Enable the {0} mods under this label; the others are left as they are.", "启用该标签下的 {0} 个 mod，其余 mod 保持不变。"),
            ["tag_only_done"] = ("{0}: {1} mods enabled", "已启用「{0}」下的 {1} 个 mod"),
            ["tag_off_all"] = ("Disable all", "批量停用"),
            ["tag_off_done"] = ("{0}: {1} mods disabled", "已停用「{0}」下的 {1} 个 mod"),
            ["tag_on_state"] = ("on", "已启用"),
            ["tag_off_state"] = ("off", "已停用"),
            ["tag_off"] = ("Take the label off {0}", "从该标签中移除 {0}"),
            ["tag_add_one"] = ("Add", "添加"),
            ["tag_added"] = ("{0} now carries {1}", "已将 {0} 加入「{1}」"),
            ["tag_none_of_mine"] = ("no label", "无标签"),
            ["tag_all_in"] = ("Every mod already carries this label", "全部 mod 均已使用该标签"),
            ["tag_foot"] = ("Labels are for sorting only: no mod file is changed.", "标签仅用于整理，不会改动 mod 文件"),
            ["tag_rest"] = ("{0} mods carry no label", "{0} 个 mod 尚未设置标签"),
            ["tag_all_done"] = ("Every mod carries a label", "全部 mod 均已设置标签"),
            ["tag_dropped"] = ("The label {0} is gone; the mods are untouched", "已删除标签「{0}」，mod 本身不受影响"),
            ["tag_on"] = ("MODS WITH THIS LABEL", "该标签下的 MOD"),
            ["tag_empty"] = ("No mod carries this label yet. Add one below.", "该标签下尚无 mod，可在下方添加。"),
            ["tag_off"] = ("Take the label off this mod", "从该 mod 上取下这个标签"),
            ["tag_put"] = ("ADD A MOD", "添加 MOD"),
            ["tag_no_hits"] = ("No mod matches", "无匹配的 mod"),
            ["tag_bare"] = ("Only mods with no label", "仅显示未设置标签的 MOD"),
            ["tag_picked"] = ("{0} selected", "已选 {0} 个"),
            ["tag_pick_all"] = ("Select all", "全选"),
            ["tag_pick_none"] = ("Clear", "清除"),
            ["tag_add_these"] = ("Add to {0}", "加入「{0}」"),
            ["tag_added_n"] = ("{0} mods now carry {1}", "已将 {0} 个 mod 加入「{1}」"),
            ["tag_tip"] = ("Your own label. The Labels page manages them.", "自行设置的标签，可在“标签”页面管理。"),
            ["tag_add"] = ("Manage labels", "管理标签"),
            ["many_tag"] = ("Manage the labels of these {0}", "管理选中的 {0} 个的标签"),
            ["deploy_busy"] = ("Another program is deploying (since {0}). The game folder is being written to; these numbers will settle when it is done.",
                               "另一个程序正在部署（{0} 开始）。游戏目录正在被写入，这些数字会在它完成后稳定下来。"),
            ["mismatch"] = ("{0} files in the game are not what they are recorded as. Something outside this program changed them; a deploy puts them right.",
                            "游戏目录中有 {0} 个文件与记录不符，是本程序以外的改动所致。部署一次即可修正。"),
            ["st_old"] = ("OLD VERSIONS LEFT OVER", "旧版本残留"),
            ["st_old_note"] = ("A mod that was replaced leaves the folder its old version was unpacked into. No mod uses these; deleting them does not touch the game.",
                               "更换版本后，旧版本解压出来的文件夹会留下。这些文件夹已无 mod 使用，删除它们不会影响游戏。"),
            ["st_old_n"] = ("{0} folders · {1}", "{0} 个文件夹 · 共 {1}"),
            ["st_old_drop"] = ("Delete the selected", "删除选中"),
            ["st_old_drop_n"] = ("Delete the selected ({0})", "删除选中的 {0} 个"),
            ["st_old_ask"] = ("Delete {0} folders of old versions? {1} comes back. The mods in the game are not touched.",
                              "删除 {0} 个旧版本文件夹？将释放 {1}。游戏中已部署的 mod 不受影响。"),
            ["st_old_done"] = ("{0} deleted, {1} came back", "已删除 {0} 个，释放 {1}"),
            ["st_import"] = ("Import from other managers…", "从其他管理器导入…"),
            ["st_default"] = ("DEFAULT", "默认"),
            ["st_game_drive"] = ("Game's drive · hard links", "游戏所在分区 · 硬链接"),
            ["st_other_drive"] = ("Another drive · copies", "其他分区 · 复制"),
            ["st_line"] = ("{0} mods · {1} · drive {2} {3} free of {4}", "{0} 个 mod · {1} · 分区 {2} 可用 {3} / 共 {4}"),
            ["st_make_default"] = ("Make default", "设为默认"),
            ["st_remove"] = ("Remove from list", "从列表移除"),
            ["st_mods_in"] = ("Mods in {0}", "{0} 中的 mod"),
            ["st_empty"] = ("This folder holds no mods of the library.", "此文件夹中没有库中的 mod。"),
            ["st_move_n"] = ("Move to… ({0})", "移动到…（{0}）"),
            ["st_move_other"] = ("Choose another folder…", "选择其他文件夹…"),
            ["st_delete_n"] = ("Delete… ({0})", "删除…（{0}）"),
            ["st_delete_ask"] = ("Delete these {0} mods from the disk for good ({1})?\n\n{2}\n\nTheir folders are deleted, not moved to the Recycle Bin. They are also taken out of the library and out of the game (a deploy runs now).",
                                 "从硬盘永久删除选中的 {0} 个 mod（共 {1}）？\n\n{2}\n\n文件夹将直接删除，不会移入回收站；这些 mod 也会从 mod 库与游戏中移除（立即执行一次部署）。"),
            ["st_deleted_n"] = ("{0} mods deleted from the disk: {1} freed", "已从硬盘删除 {0} 个 mod，释放 {1}"),
            ["st_delete_failed"] = ("These folders could not be deleted (the mods are out of the library and the game):\n{0}", "以下文件夹未能删除（对应的 mod 已从 mod 库与游戏中移除）：\n{0}"),
            ["pick_all"] = ("Select all", "全选"),
            ["st_far"] = ("{0} mods are on drive {1}, the game on {2}", "{0} 个 mod 位于分区 {1}，游戏位于分区 {2}"),
            ["st_far_why"] = ("Mods on another drive than the game are deployed as copies: they take their space a second time, and every change writes them again. On the game's drive they are hard links.",
                              "与游戏不在同一分区的 mod 只能以复制方式部署：额外占用一份空间，每次变更都需重新写入。位于游戏所在分区时可使用硬链接。"),
            ["st_far_move"] = ("Move to {0}…", "移动到 {0}…"),
            ["st_far_keep"] = ("Keep them where they are", "保持现有位置"),
            // moving mods
            ["mv_ask"] = ("Move {0} mods to:\n{1}", "将 {0} 个 mod 移动到：\n{1}"),
            ["mv_why"] = ("On the game's drive, mods are deployed as hard links: the game folder takes no extra space and a deploy writes almost nothing. On another drive they are copies, written again with every change.",
                          "位于游戏所在分区时，mod 以硬链接部署：游戏目录不占用额外空间，部署几乎不写入硬盘。位于其他分区时只能复制，每次变更都需重新写入。"),
            ["mv_how"] = ("Move: the old folders are deleted. Copy: they stay, for a manager that still uses them (the mods then take space twice).",
                          "移动：原文件夹将被删除。复制：原文件夹保留，供仍在使用的其他管理器使用（mod 将占用两份空间）。"),
            ["mv_move"] = ("Move", "移动"),
            ["mv_copy"] = ("Copy", "复制"),
            ["mv_done_move"] = ("{0} mods moved to {1}. The old folders are deleted.", "已将 {0} 个 mod 移动到 {1}，原文件夹已删除。"),
            ["mv_done_copy"] = ("{0} mods copied to {1}. The old folders are kept.", "已将 {0} 个 mod 复制到 {1}，原文件夹已保留。"),
            ["mv_foot_move"] = ("{0} mods moved to {1}", "已移动 {0} 个 mod 到 {1}"),
            ["mv_foot_copy"] = ("{0} mods copied to {1}", "已复制 {0} 个 mod 到 {1}"),
            ["mv_linked"] = ("{0} copies in the game folder are hard links now: {1} freed.", "游戏目录中 {0} 个复制件已改为硬链接，释放 {1}。"),
            ["mv_link_later"] = ("The game folder still holds copies (the game is running?): use Make hard links on the Deploy page later.", "游戏目录中仍为复制件（游戏可能正在运行）：之后可在部署页使用“改为硬链接”。"),
            // importing from other managers
            ["im_none"] = ("No other mod manager with mods was found on this computer.", "未在本机找到含有 mod 的其他管理器。"),
            ["im_ask_first"] = ("Mod managers found on this computer. Import their lists (mods, load order, on/off, options)? Their files are only read; mods found in more than one are imported once.",
                                "在本机找到以下 mod 管理器。是否导入其列表（mod、加载顺序、开关、可选内容）？仅读取其文件；同一个 mod 只导入一次。"),
            ["im_ask"] = ("Import from these managers? Mods already in the library are skipped; their files are only read.", "从以下管理器导入？库中已有的 mod 将跳过；仅读取其文件。"),
            ["im_item"] = ("{0}: {1} mods ({2})", "{0}：{1} 个 mod（{2}）"),
            ["im_done"] = ("{0} mods imported ({1} on).", "已导入 {0} 个 mod（{1} 个启用）。"),
            ["im_arsenal_top"] = ("HD2 Arsenal gives its first mod the highest priority. The load order was converted, so the same mods win as in HD2 Arsenal. To show the highest priority at the top of the list, change it in Settings.",
                                  "HD2 Arsenal 设置为“将第一个模组设置为最高优先级”。已按此换算加载顺序，覆盖关系与在 HD2 Arsenal 中相同。如需将最高优先级显示在列表顶部，可在“设置”中更改。"),
            ["im_cross"] = ("{0} conflicts between mods from different managers. The mod later in the load order, the one imported later, wins. The Conflicts filter shows them.",
                            "来自不同管理器的 mod 之间有 {0} 处冲突，加载顺序靠后（后导入）的 mod 生效。可在“冲突”筛选中查看。"),
            ["arsenal_only_one"] = ("From now on, deploy with this program only: a deploy from another manager overwrites this one's.", "此后请仅使用本程序部署：使用其他管理器部署会覆盖本程序的部署。"),
            // mods on another drive
            ["adv_text"] = ("{0} of the mods just added are on drive {1}; the game is on drive {2}.", "刚添加的 mod 中有 {0} 个位于分区 {1}，游戏位于分区 {2}。"),
            ["adv_where"] = ("They can be moved to:\n{0}", "可移动到：\n{0}"),
            ["adv_move"] = ("Move", "移动"),
            ["adv_copy"] = ("Copy (keep the originals)", "复制（保留原文件夹）"),
            ["adv_keep"] = ("Keep where they are", "保持现有位置"),
            ["adv_kept"] = ("The mods stay where they are and are deployed as copies. They can be moved on the Storage page at any time.", "mod 保持现有位置，以复制方式部署。之后可随时在“存储”页移动。"),
            ["search_hint"] = ("Search names, descriptions, options, folders, patch files, Nexus ids…", "搜索名称、说明、可选内容、文件夹、patch 文件、Nexus 编号…"),
            ["search_none"] = ("No mod matches the search.", "没有符合搜索的 mod。"),
            ["dl_not_zip"] = ("The download could not be unpacked.", "下载的文件无法解压。"),
            ["nx_not_zip"] = ("The download could not be unpacked:\n{0}", "下载的文件无法解压：\n{0}"),
            ["loader_tag"] = ("LOADER", "LOADER"),
            ["loader_tip"] = ("This mod replaces one of the game's own scripts, the way a mod loader does. Two mods that replace the same one cannot both have their way: the last in the load order wins.",
                              "该 mod 替换了游戏自带的脚本，与 mod 加载器的做法相同。两个替换同一脚本的 mod 无法同时生效，以加载顺序靠后者为准。"),
            ["lua_tip"] = ("This mod runs Lua scripts in the game.", "该 mod 会在游戏中运行 Lua 脚本。"),
            ["launch_ask"] = ("{0} changes are not deployed yet. Deploy them first, then launch the game?", "还有 {0} 项变更尚未部署。是否先部署再启动游戏？"),
            ["launch_ask_1"] = ("1 change is not deployed yet. Deploy it first, then launch the game?", "还有 1 项变更尚未部署。是否先部署再启动游戏？"),
            ["deploy_launch"] = ("Deploy and launch", "部署并启动"),
            ["game_already"] = ("The game is already running.", "游戏已经在运行。"),
            ["launching"] = ("Launching the game through Steam…", "正在通过 Steam 启动游戏…"),
            ["no_steam"] = ("The game could not be launched through Steam. Is Steam installed?", "无法通过 Steam 启动游戏，请确认已安装 Steam。"),
            ["took"] = ("{0} · took {1} s", "{0} · 用时 {1} 秒"),
            ["deployed"] = ("kept {0}, renamed {1}, wrote {2} ({3} hard-linked, {4} copied), deleted {5}; {6} files in the game",
                            "保留 {0}，改名 {1}，新增 {2}（硬链接 {3}，复制 {4}），删除 {5}；游戏目录共 {6} 个文件"),
            ["purged"] = ("deleted {0} files: the game is vanilla", "已删除 {0} 个文件，游戏已恢复原版"),
            ["purged_1"] = ("deleted 1 file: the game is vanilla", "已删除 1 个文件，游戏已恢复原版"),
            ["relinked"] = ("{0} copies are hard links now: {1} freed, nothing written", "{0} 个复制件已改为硬链接，释放 {1}，未写入硬盘"),
            ["relinked_1"] = ("1 copy is a hard link now: {1} freed, nothing written", "1 个复制件已改为硬链接，释放 {1}，未写入硬盘"),
            ["game_running"] = ("The game is running: close it first.", "游戏正在运行，请先关闭游戏。"),
            ["done"] = ("Done: {0}", "完成：{0}"),
            ["failed"] = ("Failed: {0}", "失败：{0}"),
            // the menus and questions
            ["open"] = ("Open its folder", "打开文件夹"),
            ["replace_zip"] = ("New version from a zip…", "换新版本（zip）…"),
            ["replace_folder"] = ("New version from a folder…", "换新版本（文件夹）…"),
            // several mods taken together (Ctrl, Shift)
            ["many_on"] = ("Turn on these {0} mods", "启用选中的 {0} 个 mod"),
            ["many_off"] = ("Turn off these {0} mods", "停用选中的 {0} 个 mod"),
            ["many_top"] = ("Move these {0} to the top", "将选中的 {0} 个移到顶部"),
            ["many_bottom"] = ("Move these {0} to the bottom", "将选中的 {0} 个移到底部"),
            ["many_remove"] = ("Remove these {0} from the library", "从库中移除选中的 {0} 个"),
            ["many_delete"] = ("Delete these {0} from the disk…", "从硬盘删除选中的 {0} 个…"),
            ["many_remove_ask"] = ("Remove these {0} mods from the library? Their files stay where they are.",
                                   "要从库中移除选中的 {0} 个 mod 吗？它们的文件会保留在原处。"),
            ["rename"] = ("Rename…", "重命名…"),
            ["rename_ask"] = ("Change the name shown. The folder and the files are not touched.", "更改显示名称，不会改动文件夹与文件。"),
            ["rename_save"] = ("Rename", "重命名"),
            ["real_name"] = ("Originally {0}", "原名：{0}"),
            ["real_name_tip"] = ("The name the mod came with; above is the name you set.",
                                 "mod 自带的名称；上方为自行设置的显示名称。"),
            ["remove"] = ("Remove from the library", "从库中移除"),
            // update checks, on each mod's own channel
            ["up_check"] = ("Check for updates", "检查更新"),
            ["up_checking_btn"] = ("Checking…", "正在检查…"),
            ["up_progress"] = ("Checking for updates: {0} / {1}", "正在检查更新：{0} / {1}"),
            // one mod's own check, from its row in the list
            ["up_check_row_tip"] = ("Check this mod for a newer version.", "检查该 mod 是否有新版本。"),
            ["up_row_update"] = ("newer version", "有新版本"),
            ["up_row_get"] = ("Version {0} is available: click to install.", "有新版本 {0}，点击安装。"),
            ["up_row_done"] = ("Updated to the newest version.", "已更新至最新版本。"),
            ["up_row_current"] = ("newest version", "已是最新"),
            ["up_row_unknown"] = ("version unknown", "版本未知"),
            ["up_row_no_source"] = ("source not recorded", "未记录来源"),
            ["up_row_no_check"] = ("no update check", "不支持检查"),
            ["up_row_error"] = ("check failed", "检查失败"),
            ["up_one_foot"] = ("{0}: {1}", "{0}：{1}"),
            ["f_updates"] = ("Updates", "有更新"),
            ["up_tag"] = ("UPDATE", "有更新"),
            ["up_tag_tip"] = ("A newer version is available on {0}.", "{0} 上有新版本。"),
            ["up_section"] = ("Updates", "更新"),
            ["d_channel"] = ("Update source", "更新来源"),
            ["d_version"] = ("Version", "版本"),
            ["d_site_version"] = ("Newest on the source page", "来源页面上的最新版本"),
            ["ver_none"] = ("Version not recorded", "未记录版本"),
            ["ver_tip"] = ("Version, from {0}", "版本，取自{0}"),
            ["ver_from_nexus"] = ("Nexus Mods", "Nexus Mods"),
            ["ver_from_github"] = ("the GitHub release", "GitHub 发布版本"),
            ["ver_from_file"] = ("the Nexus Mods download's name", "Nexus Mods 下载文件名"),
            ["ver_from_name"] = ("the mod's name", "mod 名称"),
            ["ver_from_folder"] = ("the folder's name", "文件夹名"),
            ["up_none"] = ("Not recorded", "未记录"),
            ["up_not_checked"] = ("Not checked yet. Use this mod's Check button in the list, or Check for updates above it.",
                                 "尚未检查。请点击这一行的“检查更新”，或列表上方的“检查更新”。"),
            ["up_new"] = ("Version {1} is available (installed: {0}).", "有新版本 {1}（当前版本 {0}）。"),
            ["up_new_unknown"] = ("The newest version on the site is {0}. The installed version is not recorded.", "网站上的最新版本为 {0}，当前版本未记录。"),
            ["up_current"] = ("This is the newest version.", "已是最新版本。"),
            ["up_no_source"] = ("The source of this mod is not recorded, so it cannot be checked. Set its source: a Nexus Mods page or a GitHub repository.",
                                "未记录此 mod 的来源，无法检查更新。请设置来源：Nexus Mods 的 mod 页面或 GitHub 仓库。"),
            ["up_no_check"] = ("{0} offers no update check. Open the page to look for a newer version.", "{0} 不提供更新检查，请打开页面查看是否有新版本。"),
            ["up_error"] = ("The check failed: {0}", "检查失败：{0}"),
            ["up_why_offline"] = ("the site could not be reached", "无法连接到网站"),
            ["up_why_missing"] = ("the repository does not exist or has no release", "仓库不存在或没有发布版本"),
            ["up_why_busy"] = ("too many requests; try again later", "请求过多，请稍后再试"),
            ["up_apply"] = ("Update", "更新"),
            ["up_apply_latest"] = ("Install the newest version", "安装最新版本"),
            ["up_page"] = ("Open page", "打开页面"),
            ["match"] = ("Match the source", "匹配来源信息"),
            ["match_tip"] = ("Ask the site what this mod is called and fill in its name, version, author, description and picture.",
                             "向来源网站查询该 mod 的名称、版本、作者、说明与图片，并填入库中。"),
            ["match_done"] = ("matched {0} against its source", "已从来源匹配 {0} 的信息"),
            ["match_done_n"] = ("matched {0} of {1} mods against their sources", "已匹配 {1} 个 mod 中的 {0} 个"),
            ["match_no_source"] = ("This mod has no Nexus Mods page or GitHub repository recorded. Set one first.",
                                   "该 mod 未记录 Nexus Mods 页面或 GitHub 仓库，请先设置来源。"),
            ["many_match"] = ("Match these {0} against their sources", "匹配选中的 {0} 个的来源信息"),
            ["by_author"] = ("by {0}", "作者：{0}"),
            ["site_words"] = ("FROM THE SITE // DESCRIPTION", "来源网站 // 说明"),
            ["up_set_source"] = ("Set source…", "设置来源…"),
            ["up_source_ask"] = ("The address of this mod's page: a Nexus Mods mod page or a GitHub repository. Any other address is kept as a page to open.",
                                 "请输入此 mod 的页面地址：Nexus Mods 的 mod 页面或 GitHub 仓库。其他地址仅保存为可打开的页面。"),
            ["up_source_save"] = ("Save", "保存"),
            ["up_source_bad"] = ("This is not a web address: {0}", "这不是有效的网址：{0}"),
            ["up_source_done"] = ("source of {0}: {1}", "已设置 {0} 的来源：{1}"),
            ["up_done"] = ("updated {0} to {1}", "已将 {0} 更新至 {1}"),
            ["up_summary"] = ("{0} mods checked. Updates available: {1}. Newest version: {2}. Version not recorded: {3}. Source not recorded: {4}.",
                              "已检查 {0} 个 mod：{1} 个有更新，{2} 个已是最新，{3} 个未记录版本，{4} 个未记录来源。"),
            ["up_summary_err"] = ("{0} checks failed; each mod's details say why.", "{0} 个 mod 检查失败，原因见各 mod 的详情。"),
            ["up_summary_other"] = ("{0} mods come from sites without an update check.", "{0} 个 mod 的来源网站不提供更新检查。"),
            ["up_summary_source"] = ("Mods without a recorded source can be given one in their details (Set source…).", "未记录来源的 mod 可在详情中通过“设置来源…”补充。"),
            ["up_summary_1"] = ("1 mod checked. Updates available: {1}. Newest version: {2}. Version not recorded: {3}. Source not recorded: {4}.",
                                "已检查 {0} 个 mod：{1} 个有更新，{2} 个已是最新，{3} 个未记录版本，{4} 个未记录来源。"),
            ["up_summary_err_1"] = ("1 check failed; the mod's details say why.", "{0} 个 mod 检查失败，原因见各 mod 的详情。"),
            ["up_summary_other_1"] = ("1 mod comes from a site without an update check.", "{0} 个 mod 的来源网站不提供更新检查。"),
            ["up_show"] = ("Show the updates", "查看更新"),
            ["up_foot"] = ("Update check finished in {1} s: {0} with updates", "检查更新完成，用时 {1} 秒：{0} 个有更新"),
            ["top"] = ("Move to the top", "移到最上"),
            ["bottom"] = ("Move to the bottom (wins)", "移到最下（优先）"),
            ["top_wins_top"] = ("Move to the top (wins)", "移到最上（优先）"),
            ["top_wins_bottom"] = ("Move to the bottom", "移到最下"),
            ["purge_ask"] = ("Restore vanilla deletes every patch file in the game folder. The library keeps its files. Continue?",
                             "恢复原版将删除游戏目录中的所有 patch 文件，mod 库不受影响。是否继续？"),
            ["remove_ask"] = ("Remove “{0}” from the library? Its files stay where they are; deploy to take it out of the game.",
                              "从库中移除「{0}」？其文件保留在原处，部署后才会从游戏中移除。"),
            ["ok"] = ("OK", "确定"),
            ["cancel"] = ("Cancel", "取消"),
            ["expand"] = ("Open", "展开"),
            ["collapse"] = ("Close", "收起"),
        };

        public static bool Has(string key) => T.ContainsKey(key);

        public static string Get(string key)
        {
            if (!T.TryGetValue(key, out var t)) return key;
            return Zh ? t.Zh : t.En;
        }

        /// <summary>A text with its values; in English a count of one ({0} is 1) takes the
        /// key's "_1" text when it has one: "1 file", not "1 files".</summary>
        public static string F(string key, params object[] args)
        {
            if (!Zh && args.Length > 0 && Convert.ToString(args[0], CultureInfo.InvariantCulture) == "1" && T.ContainsKey(key + "_1"))
                key += "_1";
            return string.Format(Get(key), args);
        }

        /// <summary>A size as the design writes it: 0 B, 812 KB, 3.4 MB, 2.84 GB.</summary>
        public static string Size(long b)
        {
            if (b <= 0) return "0 B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString(b < 10 * 1024 ? "0.0" : "0", CultureInfo.InvariantCulture) + " KB";
            double mb = b / 1048576.0;
            if (mb < 1000) return mb.ToString(mb < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB";
            return (mb / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        public static string N(int n) => n.ToString("#,0", CultureInfo.InvariantCulture);
    }
}
