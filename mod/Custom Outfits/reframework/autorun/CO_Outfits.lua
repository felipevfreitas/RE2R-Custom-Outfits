-- CO_Outfits.lua: Custom Outfits for Resident Evil 2 Remake.
-- Reads reframework/data/CustomOutfits/outfits/*.json (converted outfits) and game/<character>.json
-- (original files of each game costume) and swaps the parts in real time, without replacing
-- original files. REFramework menu (Insert) -> "Custom Outfits", or the separate window.
--
-- Per character (Claire, Leon):
--   * Body - Normal .......... used in the normal family (Claire/Leon 0-4), follows the campaign
--   * Body - Alternative ..... used in the alternative family (5-9), follows the campaign
--   * Face / Hair ............ follow the campaign (or the specific costume)
--   * [x] Specific costume ... (unticked = off) same as picking a costume in the game's costume
--                              menu: only the options that menu offers, overrides Normal and
--                              Alternative, doesn't follow the campaign, and face/hair follow that costume.
-- Choices apply to the character wherever they appear: playing as them or as an NPC/cutscene in
-- the other campaign (every object with SurvivorCostumeChanger in the scene), like Fluffy would.
--
-- Outfit format (v2): { id, name, character, part, families = {"normal","alt","extra"}, default,
--   costume_parts?, variants = { { key, label, costumes, direct, replaces, mdf, mesh, skin_mesh, chain, textures } } }
--
-- Safety rules (learned while testing):
--   * holders are created when applying and get add_ref (otherwise the game's GC frees them);
--   * never touch the character while the game is building the costume (NowChanging), except from the
--     onInstantiateAction hook, which applies to the freshly created part and only with loaded resources;
--   * textures are loaded before the material; every variant of the chosen outfits is
--     preloaded (one per frame);
--   * one swap per request, minimum gap between swaps of the same part, part ready and stable;
--   * game objects are not kept between frames (they are looked up again on every check);
--   * any failure is caught, logged, and blocks new attempts for a while.

local SETTINGS_FILE = "CustomOutfits\\settings.json"
local OUTFITS_GLOB = "CustomOutfits.*outfits.*json"
local PARTS = { "body", "face", "hair" }
local PART_GO = { body = "Body", face = "Face", hair = "Hair" }
local PART_BY_GO = { Body = "body", Face = "face", Hair = "hair" }
-- Character GameObject -> id. Groups without a known name (extra modes, Ghost Survivors, NPCs) use their
-- own code (e.g. "pl7500") if their game table exists (game/<code>.json).
local CHARS = { pl1000 = "claire", pl0000 = "leon", pl2000 = "ada", pl3000 = "sherry", pl4000 = "hunk", pl4100 = "tofu",
	pl5000 = "kendo", pl6400 = "katherine", pl5600 = "ghost" }
local CHAR_NAMES = { claire = "Claire", leon = "Leon", ada = "Ada", sherry = "Sherry", hunk = "Hunk", tofu = "Tofu",
	kendo = "Robert Kendo", katherine = "Katherine (Runaway)", ghost = "Forgotten Soldier" }
local CHAR_LIST = { "claire", "leon" } -- main menu
local OTHER_BASE = { "ada", "sherry", "hunk", "tofu", "kendo", "katherine", "ghost" } -- always under "Other characters"
local CHANGER_TYPE = "app.ropeway.survivor.SurvivorCostumeChanger"
local DEFAULT_FAMILIES = {
	claire = { normal = { 0, 1, 2, 3, 4 }, alt = { 5, 6, 7, 8, 9 } },
	leon = { normal = { 0, 1, 2, 3, 4 }, alt = { 5, 6, 7, 8, 9 } },
	ada = { normal = { 0, 1, 2, 5, 6, 7 } }, -- 5-7 reuse the normal files: no alternative
	sherry = { normal = { 0, 1, 2, 3 }, alt = { 5, 6, 7, 8 } },
}
local RES_TYPES = {
	mesh  = { "via.render.MeshResource", "via.render.MeshResourceHolder" },
	mdf   = { "via.render.MeshMaterialResource", "via.render.MeshMaterialResourceHolder" },
	skin  = { "via.render.MeshResource", "via.render.MeshResourceHolder" },
	chain = { "via.motion.ChainResource", "via.motion.ChainResourceHolder" },
	tex   = { "via.render.TextureResource", "via.render.TextureResourceHolder" },
}

local CHECK_EVERY = 10     -- frames between checks when no swap is pending
local MIN_GAP = 1.5        -- minimum seconds between two swaps of the same part
local READY_STABLE = 0.5   -- seconds the part must stay ready before swapping
local AUTO_STABLE = 2.0    -- same, for automatic applying (game start / the hook didn't apply)
local STUCK_FORCE = 6.0    -- seconds; part stuck and never ready: apply the new choice anyway
local CONFIRM_GIVEUP = 8.0 -- seconds waiting for the game to confirm the swap before freeing the selector
local JOB_TIMEOUT = 25     -- seconds before giving up on a request
local FAIL_BLOCK = 30      -- seconds without retrying after a failure
local SETTLE = 1.0         -- seconds after loading a variant before the hook may use it

-- REFramework's json saves an empty table as null: everything read goes through as_table
local function as_table(v) if type(v) == "table" then return v end return {} end
local settings = as_table(json.load_file(SETTINGS_FILE))
settings.selected = as_table(settings.selected) -- selected[char] = { body_normal, body_alt, body_specific, specific_on, face, hair }
settings.orig = as_table(settings.orig)         -- learned originals (fallback for the game table)
if settings.instant == nil then settings.instant = true end
if settings.show_window == nil then settings.show_window = true end

local catalog, by_id = {}, {}
local views = {}         -- outfit as seen by another character (outfit_for)
local scanned = false
local game_tables = {}   -- char -> game table (game/<char>.json) or false
local resources = {}     -- "kind|path" -> resource (with add_ref)
local loaded_at = {}     -- variant mdf -> os.clock() when it finished loading
local preload_queue = {} -- variants waiting to be preloaded (one per frame)
local queued = {}
local live_holders = {}
-- state per "character|part" (the player and the NPCs are different characters)
local jobs = {}          -- jobs[k] = { char, part, o, v, stage, t0, t1, auto, restore, ready_since, first }
local blocked_until = {}
local last_set = {}
local touched = {}
local started = {}       -- characters whose initial preload was already queued
local status = "ready"
local frame = 0
local force_check = false
local seen_now = {}      -- characters in the scene at the last check -> "player"/"npc"
local player_char, player_costume = nil, nil

local function log_(s) log.info("[CO] " .. s) end
local function set_status(s) status = s; log_(s) end
local function try(f, ...) local ok, r = pcall(f, ...); if ok then return r end end
local function save() json.dump_file(SETTINGS_FILE, settings) end
local function is_custom(p) return p ~= nil and p:lower():find("^custom_outfits/") ~= nil end
local function same(a, b) return a ~= nil and b ~= nil and a:lower() == b:lower() end
local function key_of(char, part) return char .. "|" .. part end
local function sel_of(char)
	settings.selected[char] = as_table(settings.selected[char])
	return settings.selected[char]
end

----------------------------------------------------------------- catalog
local function normalize(j)
	if type(j) ~= "table" or not j.id or not j.character or not PART_GO[j.part] then return nil end
	if type(j.variants) ~= "table" then
		if not j.mdf then return nil end
		j.variants = { { key = j.id, mdf = j.mdf, mesh = j.mesh, skin_mesh = j.skin_mesh, chain = j.chain, textures = j.textures } }
		j.default = 0
	end
	if #j.variants == 0 then return nil end
	for _, v in ipairs(j.variants) do
		if type(v) ~= "table" or not v.mdf then return nil end
		v.costume_set = {}
		for _, c in ipairs(type(v.costumes) == "table" and v.costumes or {}) do v.costume_set[c] = true end
		-- also = { character = {costumes} }: other characters that use the same file (e.g. Hunk's body
		-- on the Forgotten Soldier). The outfit applies to them too, like Fluffy would.
		v.also_set = {}
		j.also_chars = j.also_chars or {}
		for ch, list in pairs(type(v.also) == "table" and v.also or {}) do
			v.also_set[ch] = {}
			j.also_chars[ch] = true
			for _, c in ipairs(type(list) == "table" and list or {}) do v.also_set[ch][c] = true end
		end
	end
	j.family_set = {}
	local fams = type(j.families) == "table" and j.families or {}
	for _, f in ipairs(fams) do j.family_set[f] = true end
	if #fams == 0 then j.family_set.normal, j.family_set.alt = true, true end -- old format: both
	return j
end

local function scan()
	catalog, by_id = {}, {}
	views = {}
	scanned = true
	local n = 0
	for _, path in ipairs(fs.glob(OUTFITS_GLOB)) do
		local j = normalize(json.load_file(path))
		if j then
			local owners = { j.character }
			for ch in pairs(j.also_chars or {}) do if ch ~= j.character then owners[#owners + 1] = ch end end
			for _, ch in ipairs(owners) do
				catalog[ch] = catalog[ch] or {}
				catalog[ch][j.part] = catalog[ch][j.part] or {}
				table.insert(catalog[ch][j.part], j)
			end
			by_id[j.id] = j
			n = n + 1
		else
			log_("invalid json: " .. tostring(path))
		end
	end
	for _, parts in pairs(catalog) do
		for _, list in pairs(parts) do
			table.sort(list, function(a, b) return (a.name or a.id) < (b.name or b.id) end)
		end
	end
	return n
end

----------------------------------------------------------------- game table
local function game_table(char)
	if game_tables[char] == nil then
		local t = json.load_file("CustomOutfits\\game\\" .. char .. ".json")
		game_tables[char] = type(t) == "table" and t or false
		if not game_tables[char] then log_("game table missing for " .. char .. " (using learned originals)") end
	end
	return game_tables[char] or nil
end

-- character id for the GameObject name (nil if it isn't a character with costumes)
local function char_of_name(name)
	if not name then return nil end
	if CHARS[name] then return CHARS[name] end
	if name:match("^pl%d%d%d%d$") and game_table(name) then return name end
	return nil
end

-- story families of the character (empty = character without a story: Hunk, Tofu, extras)
local function families_of(char)
	local gt = game_table(char)
	return (gt and type(gt.families) == "table") and gt.families or DEFAULT_FAMILIES[char] or {}
end

local function family_of(char, costume)
	local gt = game_table(char)
	local fams = (gt and type(gt.families) == "table") and gt.families or DEFAULT_FAMILIES[char] or {}
	for name, list in pairs(fams) do
		if type(list) == "table" then
			for _, c in ipairs(list) do if c == costume then return name end end
		end
	end
	return nil -- unlockable / extra costume
end

-- original files of a part in a costume: game table, else what the script learned
local function game_orig(char, costume, part)
	local gt = game_table(char)
	local byc = gt and type(gt.costumes) == "table" and gt.costumes[tostring(costume)]
	local op = type(byc) == "table" and byc[part]
	if type(op) == "table" and op.mdf then return op end
	local l = settings.orig[char]
	l = type(l) == "table" and l[tostring(costume)]
	l = type(l) == "table" and l[part]
	return type(l) == "table" and l or nil
end

local function remember_orig(char, costume, part, st)
	if not (char and costume and st.mdf) or is_custom(st.mdf) then return end
	local k = tostring(costume)
	settings.orig[char] = as_table(settings.orig[char])
	settings.orig[char][k] = as_table(settings.orig[char][k])
	local cur = settings.orig[char][k][part]
	if type(cur) == "table" and cur.mdf == st.mdf and cur.mesh == st.mesh and cur.skin == st.skin and cur.chain == st.chain then return end
	settings.orig[char][k][part] = { mesh = st.mesh, mdf = st.mdf, skin = st.skin, chain = st.chain }
	save()
end

----------------------------------------------------------------- game
local function get_player()
	local pm = sdk.get_managed_singleton(sdk.game_namespace("PlayerManager"))
	return pm and pm:call("get_CurrentPlayer")
end

local function get_costume(ch)
	return ch and try(function() return ch:call("get_CurrentCostume") end)
end

-- every character with costumes in the scene (player and NPCs). Don't keep between frames.
local function survivors()
	local out, have = {}, {}
	local scene = try(function()
		return sdk.call_native_func(sdk.get_native_singleton("via.SceneManager"),
			sdk.find_type_definition("via.SceneManager"), "get_CurrentScene()")
	end)
	local arr = scene and try(function() return scene:call("findComponents(System.Type)", sdk.typeof(CHANGER_TYPE)) end)
	local elems = arr and try(function() return arr:get_elements() end) or {}
	local player = try(get_player)
	local player_name = player and try(function() return player:get_Name() end)
	for _, ch in ipairs(elems) do
		local go = try(function() return ch:call("get_GameObject") end)
		local name = go and try(function() return go:get_Name() end)
		local char = char_of_name(name)
		if char and not have[char] then
			have[char] = true
			out[#out + 1] = { ch = ch, go = go, char = char, is_player = (name == player_name) }
		end
	end
	-- fallback: if the scene search fails, at least the player
	local pchar = char_of_name(player_name)
	if player and pchar and not have[pchar] then
		local ch = try(function() return player:call("getComponent(System.Type)", sdk.typeof(CHANGER_TYPE)) end)
		if ch then out[#out + 1] = { ch = ch, go = player, char = pchar, is_player = true } end
	end
	return out
end

local function part_components(go)
	local function comp(t) return go:call("getComponent(System.Type)", sdk.typeof(t)) end
	local mesh = comp("via.render.Mesh")
	if not mesh then return nil end
	return { mesh = mesh, skin = comp("via.render.ComputeSkinning"), chain = comp("via.motion.Chain") }
end

-- components of a character part (owner = its GameObject); nil if not found
local function find_part(owner, go_name)
	return try(function()
		local child = owner:get_Transform():get_Child()
		while child do
			local go = child:get_GameObject()
			if go:get_Name() == go_name then return part_components(go) end
			child = child:get_Next()
		end
	end)
end

local function holder_path(h) return h and try(function() return h:call("get_ResourcePath") end) end

local function read_state(p)
	return {
		mesh = holder_path(try(function() return p.mesh:call("getMesh") end)),
		mdf = holder_path(try(function() return p.mesh:call("get_Material") end)),
		skin = p.skin and holder_path(try(function() return p.skin:call("get_Mesh") end)),
		chain = p.chain and holder_path(try(function() return p.chain:call("get_ChainAsset") end)),
	}
end

local function comp_ready(p)
	local a = try(function() return p.mesh:call("get_MeshReady") end)
	local b = try(function() return p.mesh:call("get_MaterialReady") end)
	return a == true and b == true, a, b
end

----------------------------------------------------------------- resources
local function get_res(kind, p)
	local key = kind .. "|" .. p:lower()
	local r = resources[key]
	if r then return r end
	r = sdk.create_resource(RES_TYPES[kind][1], p)
	if not r then error(kind .. " failed to load: " .. p) end
	r:add_ref()
	resources[key] = r
	return r
end

local function new_holder(kind, p)
	local h = get_res(kind, p):create_holder(RES_TYPES[kind][2])
	h:add_ref()
	live_holders[#live_holders + 1] = h
	return h
end

-- final paths of each component for variant v (orig = original files of the part)
local function targets(v, orig)
	local t = { mesh = v.mesh, mdf = v.mdf, chain = v.chain }
	if v.skin_mesh then
		t.skin = v.skin_mesh
	elseif v.mesh and orig and same(orig.skin, orig.mesh) then
		t.skin = v.mesh -- body/hair: ComputeSkinning uses the same mesh that is drawn
	elseif orig then
		t.skin = orig.skin
	end
	if not t.chain and orig then t.chain = orig.chain end
	return t
end

-- loads the variant's mesh/mdf2/skin/chain and textures (the game loads them right away: the "hitch")
local function load_now(v)
	if loaded_at[v.mdf] then return end
	local t = targets(v, nil)
	for _, kind in ipairs({ "mesh", "mdf", "skin", "chain" }) do
		if t[kind] then get_res(kind, t[kind]) end
	end
	if type(v.textures) == "table" then
		for _, p in ipairs(v.textures) do get_res("tex", p) end
	end
	loaded_at[v.mdf] = os.clock()
end

-- queues every variant of the outfit (preloading spread out, one per frame)
local function queue_outfit(o)
	if not o then return end
	for _, v in ipairs(o.variants) do
		if not loaded_at[v.mdf] and not queued[v.mdf] then
			queued[v.mdf] = true
			preload_queue[#preload_queue + 1] = v
		end
	end
end

local function do_apply(p, t)
	if t.mesh then p.mesh:call("setMesh", new_holder("mesh", t.mesh)) end
	p.mesh:call("set_Material", new_holder("mdf", t.mdf))
	local cur = read_state(p)
	if t.skin and p.skin and not same(cur.skin, t.skin) then p.skin:call("set_Mesh", new_holder("skin", t.skin)) end
	if t.chain and p.chain and not same(cur.chain, t.chain) then p.chain:call("set_ChainAsset", new_holder("chain", t.chain)) end
end

----------------------------------------------------------------- what each part should wear
local function choose_variant(o, costume)
	if costume ~= nil then
		for _, v in ipairs(o.variants) do
			if v.costume_set and v.costume_set[costume] then return v end
		end
	end
	return o.variants[(tonumber(o.default) or 0) + 1] or o.variants[1]
end

local function orig_outfit(char, costume, part, op)
	local o = normalize({ id = ("orig:%s:%s:%s"):format(char, tostring(costume), part), name = "original",
		character = char, part = part, mesh = op.mesh, mdf = op.mdf, skin_mesh = op.skin, chain = op.chain })
	if o then o.is_orig = true; o.variants[1].label = "original" end
	return o
end

-- The same outfit seen by another character that uses the file (also): variants with their costumes.
local function outfit_for(o, char)
	if not o or o.character == char then return o end
	if not (o.also_chars and o.also_chars[char]) then return nil end
	local key = o.id .. "@" .. char
	if views[key] then return views[key] end
	local vs, def = {}, nil
	for _, v in ipairs(o.variants) do
		if v.also_set[char] then
			local copy = {}
			for k2, val in pairs(v) do copy[k2] = val end
			copy.costume_set = v.also_set[char]
			vs[#vs + 1] = copy
			def = def or (#vs - 1)
		end
	end
	if #vs == 0 then return nil end
	views[key] = { id = o.id, name = o.name, character = char, part = o.part, variants = vs, default = def,
		family_set = o.family_set, costume_parts = o.costume_parts, also_chars = {} }
	return views[key]
end

-- Specific costume: box ticked + choice "<outfit id>|<game menu costume>". Returns (outfit, costume).
-- Only exists for characters with an alternative costume: without it the body is always the same and the
-- specific costume would give the same result as the body selector.
local function specific_outfit(char)
	local sel = sel_of(char)
	if not sel.specific_on or not families_of(char).alt then return nil end
	local key = sel.body_specific
	if type(key) ~= "string" then return nil end
	local id, c = key:match("^(.*)|(%d+)$")
	local o = outfit_for(id and by_id[id], char)
	if not o then return nil end
	return o, tonumber(c)
end

-- Returns (outfit, effective costume) the part should wear now, or nil to leave it as the game wants.
local function desired(char, costume, part, cur_mdf)
	local sel = sel_of(char)
	local spec, spec_c = specific_outfit(char)
	if part == "body" then
		if spec then return spec, spec_c end
		local id
		if next(families_of(char)) == nil then
			id = sel.body_normal -- character without a story: a single "Body" selector for any costume
		else
			local fam = family_of(char, costume)
			id = (fam == "normal" and sel.body_normal) or (fam == "alt" and sel.body_alt) or nil
		end
		local o = outfit_for(id and by_id[id], char)
		if o then return o, costume end
	else
		local eff = spec and spec_c or costume
		local o = outfit_for(by_id[sel[part] or ""], char)
		if o then return o, eff end
		if spec then
			-- no outfit chosen for this part: what this costume uses (the mod's prefabs, else the game)
			local cps = type(spec.costume_parts) == "table" and spec.costume_parts[tostring(spec_c)]
			local op = (type(cps) == "table" and type(cps[part]) == "table" and cps[part].mdf and cps[part])
				or game_orig(char, spec_c, part)
			if op then return orig_outfit(char, spec_c, part, op), spec_c end
		end
	end
	-- nothing chosen: if the script changed this part, return the original of the current costume
	if is_custom(cur_mdf) or touched[key_of(char, part)] then
		local op = game_orig(char, costume, part)
		if op and not same(cur_mdf, op.mdf) then return orig_outfit(char, costume, part, op), costume end
	end
	return nil
end

----------------------------------------------------------------- requests
local function fail_job(k, job, why)
	set_status(("failed %s (%s): %s"):format(k, job.o and (job.o.name or job.o.id) or "?", why))
	jobs[k] = nil
	blocked_until[k] = os.clock() + FAIL_BLOCK
end

local function request(char, part, o, eff, auto)
	local k = key_of(char, part)
	local job = { char = char, part = part, o = o, stage = "wait", t0 = os.clock(), auto = auto, restore = o.is_orig }
	jobs[k] = job
	local ok, err = pcall(function()
		job.v = choose_variant(o, eff)
		job.first = not loaded_at[job.v.mdf]
		load_now(job.v)
	end)
	if not ok then fail_job(k, job, tostring(err)); return nil end
	return job
end

local function apply_variant(p, char, costume, part, v)
	local st = read_state(p)
	remember_orig(char, costume, part, st)
	local op = game_orig(char, costume, part)
	do_apply(p, targets(v, op or st))
	local k = key_of(char, part)
	-- ticked only while the part differs from this costume's original
	touched[k] = not (op and same(v.mdf, op.mdf))
	last_set[k] = os.clock()
end

local function process_jobs(s, changing, costume)
	local now = os.clock()
	for _, part in ipairs(PARTS) do
		local k = key_of(s.char, part)
		local job = jobs[k]
		if job then
			local p = find_part(s.go, PART_GO[part])
			if changing then job.ready_since = nil end -- part being rebuilt: restart the count
			if now - job.t0 > JOB_TIMEOUT then
				local _r, mr, mt
				if p then _r, mr, mt = comp_ready(p) end
				fail_job(k, job, ("timed out stage=%s MeshReady=%s MaterialReady=%s"):format(
					job.stage, tostring(mr), tostring(mt)))
			elseif p and not changing then
				local ready, mr, mt = comp_ready(p)
				if ready then job.ready_since = job.ready_since or now else job.ready_since = nil end
				local stable_for = job.ready_since and (now - job.ready_since) or 0
				if job.stage == "wait" then
					local gap_ok = now - (last_set[k] or -1000) >= MIN_GAP
					local need = job.auto and AUTO_STABLE or READY_STABLE
					local stuck = (not ready) and (not job.auto) and (now - job.t0) >= STUCK_FORCE
					if gap_ok and ((ready and stable_for >= need) or stuck) then
						-- the choice or the stage may have changed since the request: recompute
						local cur = read_state(p).mdf
						local o, eff = desired(s.char, costume, part, cur)
						if not o then
							jobs[k] = nil
						else
							local ok, err = pcall(function()
								job.o, job.restore = o, o.is_orig
								job.v = choose_variant(o, eff)
								load_now(job.v)
								if not same(cur, job.v.mdf) then apply_variant(p, s.char, costume, part, job.v) end
							end)
							if not ok then
								fail_job(k, job, "apply: " .. tostring(err))
							else
								job.stage, job.t1 = "applied", now
								log_(("applied %s%s: %s [%s] costume=%s (first=%s auto=%s forced=%s)"):format(k,
									s.is_player and "" or " (NPC)", o.name or o.id, tostring(job.v.label or job.v.key),
									tostring(costume), tostring(job.first), tostring(job.auto), tostring(stuck)))
							end
						end
					end
				elseif job.stage == "applied" then
					if ready and now - job.t1 >= READY_STABLE then
						jobs[k] = nil
						set_status((job.restore and "original: " or "done: ") .. k .. " = " .. (job.o.name or job.o.id)
							.. " (" .. tostring(job.v.label or job.v.key) .. ")")
					elseif now - job.t1 >= CONFIRM_GIVEUP then
						jobs[k] = nil
						set_status(("%s applied, but the game has not confirmed yet (MeshReady=%s MaterialReady=%s)"):format(
							k, tostring(mr), tostring(mt)))
					end
				end
			end
		end
	end
end

----------------------------------------------------------------- loop
local function queue_selected(char)
	local sel = sel_of(char)
	for _, k in ipairs({ "body_normal", "body_alt", "face", "hair" }) do queue_outfit(by_id[sel[k] or ""]) end
	local spec = specific_outfit(char)
	if spec then queue_outfit(spec) end
end

local function tick()
	local list = survivors()
	local enforce = force_check or frame % CHECK_EVERY == 0
	local auto = not force_check
	force_check = false
	local now = os.clock()
	local present = {}
	for _, s in ipairs(list) do
		present[s.char] = s.is_player and "jogador" or "npc"
		if not started[s.char] then started[s.char] = true; queue_selected(s.char) end
		local changing = try(function() return s.ch:call("get_NowChanging") end) ~= false
		local costume = get_costume(s.ch)
		if s.is_player then player_char, player_costume = s.char, costume end
		process_jobs(s, changing, costume)
		if enforce and not changing then
			for _, part in ipairs(PARTS) do
				local k = key_of(s.char, part)
				if not jobs[k] and (blocked_until[k] or 0) < now then
					local p = find_part(s.go, PART_GO[part])
					local cur = p and read_state(p).mdf
					if cur then
						local o, eff = desired(s.char, costume, part, cur)
						if o and not same(cur, choose_variant(o, eff).mdf) then request(s.char, part, o, eff, auto) end
					end
				end
			end
		end
	end
	-- requests for characters that left the scene: drop them
	for k, job in pairs(jobs) do
		if not present[job.char] then jobs[k] = nil end
	end
	seen_now = present
end

----------------------------------------------------------------- game costume change (no flash)
-- The game creates each new part in SurvivorCostumeChanger.onInstantiateAction(GameObject, Parts), for
-- both the player and the NPCs. Applying right after, the part is born with the mod's outfit. Only uses
-- variants loaded at least SETTLE seconds ago; otherwise leaves it to the normal swap.
local hook_stack = {}

local function apply_on_instantiate(changer, go)
	local owner = changer:call("get_GameObject")
	local char = char_of_name(owner:get_Name())
	local part = PART_BY_GO[go:get_Name()]
	if not (char and part) then return end
	local p = part_components(go)
	if not p then return end
	local costume = get_costume(changer)
	local cur = read_state(p).mdf
	local o, eff = desired(char, costume, part, cur)
	if not o then return end
	local v = choose_variant(o, eff)
	if same(cur, v.mdf) then return end
	local t = loaded_at[v.mdf]
	if not t or os.clock() - t < SETTLE then
		queue_outfit(o)
		log_(("hook: %s [%s] still loading, regular swap takes over"):format(key_of(char, part), tostring(v.label or v.key)))
		return
	end
	apply_variant(p, char, costume, part, v)
	jobs[key_of(char, part)] = nil
	log_(("instant %s: %s [%s] costume=%s"):format(key_of(char, part), o.name or o.id, tostring(v.label or v.key), tostring(costume)))
end

local changer_td = sdk.find_type_definition(CHANGER_TYPE)
local inst_method = changer_td and changer_td:get_method("onInstantiateAction(via.GameObject, app.ropeway.SurvivorDefine.Parts)")
if inst_method then
	sdk.hook(inst_method,
		function(args)
			hook_stack[#hook_stack + 1] = settings.instant and { this = args[2], go = args[3] } or false
		end,
		function(retval)
			local c = table.remove(hook_stack)
			if c then
				local ok, err = pcall(function()
					apply_on_instantiate(sdk.to_managed_object(c.this), sdk.to_managed_object(c.go))
				end)
				if not ok then log_("instant swap failed (regular swap takes over): " .. tostring(err)) end
			end
			return retval
		end)
	log_("instant swap hook installed")
else
	log_("onInstantiateAction not found: regular swap only")
end

----------------------------------------------------------------- debug
-- Diagnostic tools. Results in reframework/data/CustomOutfits/*.txt and in the log ([CO]).
local draw_debug
do
	local out_lines = {}
	local count = 0
	local test_costume = 0

	local function emit(s)
		out_lines[#out_lines + 1] = s
		log.info("[CO] " .. s)
	end

	local function try(f, ...)
		local ok, r = pcall(f, ...)
		if ok then return r end
		return nil
	end

	local function tname(obj)
		local td = try(function() return obj:get_type_definition() end)
		return td and td:get_full_name() or "?"
	end

	local function res_path(res)
		if not res then return "nil" end
		return try(function() return res:call("get_ResourcePath") end)
			or try(function() return res:call("ToString()") end)
			or "?"
	end

	local function dump_obj(go, depth)
		local pad = string.rep("  ", depth)
		local name = try(function() return go:get_Name() end) or "?"
		emit(pad .. "GO: " .. tostring(name) .. " (active=" .. tostring(try(function() return go:get_DrawSelf() end)) .. ")")
		local comps = try(function() return go:call("get_Components") end)
		local elems = comps and try(function() return comps:get_elements() end) or {}
		for _, c in ipairs(elems) do
			local tn = tname(c)
			emit(pad .. "  comp: " .. tn)
			if tn:find("SurvivorCostumeChanger") or tn:find("SurvivorMeshPartsController") then
				local td = c:get_type_definition()
				for _, fld in ipairs(td:get_fields()) do
					local v = try(function() return fld:get_data(c) end)
					emit(pad .. "    f " .. fld:get_name() .. " : " .. tostring(fld:get_type():get_full_name()) .. " = " .. tostring(v))
				end
				for _, mth in ipairs(td:get_methods()) do
					emit(pad .. "    m " .. mth:get_name())
				end
			end
			if tn == "via.render.Mesh" then
				local m = try(function() return c:call("getMesh") end)
				emit(pad .. "    mesh obj: " .. (m and tname(m) or "nil")
					.. " | ResourcePath=" .. tostring(m and try(function() return m:call("get_ResourcePath") end))
					.. " | ToString=" .. tostring(m and try(function() return m:call("ToString()") end))
					.. " | Path=" .. tostring(m and try(function() return m:call("get_Path") end)))
				local f = try(function() return c:get_field("<Mesh>k__BackingField") end)
				emit(pad .. "    field Mesh: " .. tostring(f))
				emit(pad .. "    mdf2: " .. tostring(res_path(try(function() return c:call("get_Material") end))))
				emit(pad .. "    materials: " .. tostring(try(function() return c:call("get_MaterialNum") end)))
			end
		end
		local xf = try(function() return go:get_Transform() end)
		local child = xf and try(function() return xf:get_Child() end)
		while child do
			local cgo = try(function() return child:get_GameObject() end)
			if cgo then dump_obj(cgo, depth + 1) end
			child = try(function() return child:get_Next() end)
		end
	end

	local function do_dump()
		out_lines = {}
		local pm = sdk.get_managed_singleton(sdk.game_namespace("PlayerManager"))
		local player = pm and pm:call("get_CurrentPlayer")
		if not player then
			emit("no player (load a game first)")
			return
		end
		emit("=== dump " .. os.date("%Y-%m-%d %H:%M:%S") .. " ===")
		dump_obj(player, 0)
		count = count + 1
		try(function() fs.write("CustomOutfits/dump_" .. count .. ".txt", table.concat(out_lines, "\n")) end)
	end

	local function do_types()
		out_lines = {}
		emit("=== types " .. os.date("%Y-%m-%d %H:%M:%S") .. " ===")
		local function enum_dump(name)
			local td = sdk.find_type_definition(name)
			emit("-- enum " .. name .. (td and "" or " (not found)"))
			if not td then return end
			for _, f in ipairs(td:get_fields()) do
				if f:is_static() then
					emit("   " .. f:get_name() .. " = " .. tostring(try(function() return f:get_data(nil) end)))
				end
			end
		end
		local function methods_dump(name)
			local td = sdk.find_type_definition(name)
			emit("-- methods " .. name .. (td and "" or " (not found)"))
			if not td then return end
			for _, m in ipairs(td:get_methods()) do
				local ps = {}
				for _, p in ipairs(m:get_param_types() or {}) do ps[#ps + 1] = p:get_full_name() end
				emit("   " .. m:get_name() .. "(" .. table.concat(ps, ", ") .. ") -> " .. tostring(m:get_return_type() and m:get_return_type():get_full_name()))
			end
		end
		enum_dump("app.ropeway.SurvivorDefine.Costume")
		enum_dump("app.ropeway.SurvivorDefine.Parts")
		enum_dump("app.ropeway.SurvivorDefine.SurvivorType")
		methods_dump("via.render.Mesh")
		methods_dump("via.render.MeshResourceHolder")
		methods_dump("via.render.MeshMaterialResourceHolder")
		methods_dump("app.ropeway.survivor.SurvivorCostumeChanger")
		count = count + 1
		try(function() fs.write("CustomOutfits/types_" .. count .. ".txt", table.concat(out_lines, "\n")) end)
	end

	-- Lists resources (chain, blend, wrinkle...) of the Face/Hair/Body components.
	local INTEREST = {
		["via.motion.Chain"] = true, ["via.render.Wrinkle"] = true, ["via.render.Stamp"] = true,
		["app.ropeway.survivor.SurvivorFacialController"] = true, ["app.ropeway.survivor.SurvivorChainController"] = true,
		["via.render.ComputeSkinning"] = true, ["via.motion.JointConstraints"] = true,
	}
	local function describe(v)
		if v == nil then return "nil" end
		if type(v) ~= "userdata" then return tostring(v) end
		local p = try(function() return v:call("get_ResourcePath") end)
		if p then return "RES[" .. p .. "]" end
		return tname(v)
	end
	local function do_parts()
		out_lines = {}
		emit("=== parts " .. os.date("%Y-%m-%d %H:%M:%S") .. " ===")
		local pm = sdk.get_managed_singleton(sdk.game_namespace("PlayerManager"))
		local player = pm and pm:call("get_CurrentPlayer")
		if not player then emit("no player"); return end
		local child = player:get_Transform():get_Child()
		while child do
			local go = child:get_GameObject()
			local name = go:get_Name()
			if name == "Face" or name == "Hair" or name == "Body" then
				emit("GO " .. name)
				local comps = go:call("get_Components"):get_elements()
				for _, c in ipairs(comps) do
					local tn = tname(c)
					if INTEREST[tn] then
						emit("  comp " .. tn)
						local td = c:get_type_definition()
						for _, f in ipairs(td:get_fields()) do
							if not f:is_static() then
								emit("    f " .. f:get_name() .. " : " .. f:get_type():get_full_name() .. " = " .. describe(try(function() return f:get_data(c) end)))
							end
						end
						for _, m in ipairs(td:get_methods()) do
							local rt = m:get_return_type()
							local rtn = rt and rt:get_full_name() or ""
							local np = #(m:get_param_types() or {})
							if np == 0 and m:get_name():find("^get") and (rtn:find("Resource") or rtn:find("Holder")) then
								emit("    m " .. m:get_name() .. "() -> " .. rtn .. " = " .. describe(try(function() return c:call(m:get_name()) end)))
							elseif m:get_name():find("^set") and (rtn:find("Resource") or table.concat((function()
								local t = {} for _, p in ipairs(m:get_param_types() or {}) do t[#t + 1] = p:get_full_name() end return t end)(), ","):find("Resource")) then
								emit("    setter " .. m:get_name())
							end
						end
					end
				end
			end
			child = child:get_Next()
		end
		count = count + 1
		try(function() fs.write("CustomOutfits/parts_" .. count .. ".txt", table.concat(out_lines, "\n")) end)
	end


	-- Lists characters in the scene (player and NPCs) for NPC support.
	local function do_scene()
		out_lines = {}
		emit("=== scene " .. os.date("%Y-%m-%d %H:%M:%S") .. " ===")
		local scene = sdk.call_native_func(sdk.get_native_singleton("via.SceneManager"), sdk.find_type_definition("via.SceneManager"), "get_CurrentScene()")
		local pm = sdk.get_managed_singleton(sdk.game_namespace("PlayerManager"))
		local player = pm and pm:call("get_CurrentPlayer")
		emit("player: " .. tostring(player and player:get_Name()))
		local chs = scene:call("findComponents(System.Type)", sdk.typeof("app.ropeway.survivor.SurvivorCostumeChanger"))
		for _, ch in ipairs(chs and chs:get_elements() or {}) do
			local go = try(function() return ch:call("get_GameObject") end)
			local parent = go and try(function() return go:get_Transform():get_Parent():get_GameObject():get_Name() end)
			emit(("changer: GO=%s parent=%s costume=%s"):format(tostring(go and go:get_Name()), tostring(parent),
				tostring(try(function() return ch:call("get_CurrentCostume") end))))
		end
		-- meshes that use Claire/Leon files (pl1000/pl0000) and don't belong to the player
		local meshes = scene:call("findComponents(System.Type)", sdk.typeof("via.render.Mesh"))
		local n = 0
		for _, m in ipairs(meshes and meshes:get_elements() or {}) do
			local mdf = try(function() return m:call("get_Material"):call("get_ResourcePath") end)
			if mdf and (mdf:find("/pl1000/") or mdf:find("/pl0000/")) then
				local go = try(function() return m:call("get_GameObject") end)
				local owner = go and try(function() return go:get_Transform():get_Parent():get_GameObject() end)
				local oname = owner and try(function() return owner:get_Name() end)
				local is_player = player and owner and try(function() return owner:get_Name() == player:get_Name() end)
				emit(("mesh: GO=%s owner=%s player=%s mdf=%s"):format(tostring(go and go:get_Name()), tostring(oname), tostring(is_player), mdf))
				n = n + 1
			end
		end
		emit("Claire/Leon meshes in scene: " .. n)
		count = count + 1
		try(function() fs.write("CustomOutfits/scene_" .. count .. ".txt", table.concat(out_lines, "\n")) end)
	end


	draw_debug = function()
		-- fast swap path (onInstantiateAction hook): turning it off is the first test if something goes wrong
		local ci, inst = imgui.checkbox("Apply on game costume change (no flash of the original outfit)", settings.instant)
		if ci then settings.instant = inst; save() end
		if imgui.button("Dump player hierarchy") then
			local ok, err = pcall(do_dump)
			if not ok then log.info("[CO] error: " .. tostring(err)) end
		end
		if imgui.button("Dump types (enums/methods)") then
			local ok, err = pcall(do_types)
			if not ok then log.info("[CO] error: " .. tostring(err)) end
		end
		if imgui.button("Dump characters in scene (NPC)") then
			local ok, err = pcall(do_scene)
			if not ok then log.info("[CO] error: " .. tostring(err)) end
		end
		if imgui.button("Dump face/hair components") then
			local ok, err = pcall(do_parts)
			if not ok then log.info("[CO] error: " .. tostring(err)) end
		end
		imgui.text("Files: reframework/data/CustomOutfits/dump_N / types_N / parts_N / scene_N .txt")
		imgui.separator()
		imgui.text("Test: change the game costume (simulates story stages)")
		local changed_c, v = imgui.slider_int("Costume##co_dbg_costume", test_costume, 0, 13)
		if changed_c then test_costume = v end
		if imgui.button("Change game costume") then
			local ok, err = pcall(function()
				local pm = sdk.get_managed_singleton(sdk.game_namespace("PlayerManager"))
				local player = pm:call("get_CurrentPlayer")
				local ch = player:call("getComponent(System.Type)", sdk.typeof("app.ropeway.survivor.SurvivorCostumeChanger"))
				ch:call("change(app.ropeway.SurvivorDefine.Costume)", test_costume)
			end)
			log.info("[CO] test: change costume to " .. test_costume .. (ok and " ok" or (" error: " .. tostring(err))))
		end
	end
end

----------------------------------------------------------------- menu
-- generic combo: entries = { {id=, name=} }; first = label of the "none" option (or nil for none).
-- Returns true and the chosen id (nil = "none" option) when it changes.
local function combo(label, first, entries, cur_id)
	local names, ids, cur = {}, {}, 1
	if first then names[1], ids[1] = first, false end
	for _, e in ipairs(entries) do
		names[#names + 1] = e.name
		ids[#ids + 1] = e.id
		if e.id == cur_id then cur = #names end
	end
	local changed, idx = imgui.combo(label, cur, names)
	if changed and idx ~= cur then return true, (ids[idx] or nil) end
	return false
end

local function set_choice(char, key, value, outfit_to_queue)
	sel_of(char)[key] = value
	save()
	queue_outfit(outfit_to_queue)
	for _, part in ipairs(PARTS) do blocked_until[key_of(char, part)] = nil end
	force_check = true
end

local function outfits_for(char, part, family)
	local out = {}
	for _, o in ipairs((catalog[char] or {})[part] or {}) do
		if not family or o.family_set[family] then out[#out + 1] = { id = o.id, name = o.name or o.id } end
	end
	return out
end

-- specific costume options: the same as the game's costume menu (one costume per different body in
-- each family the outfit replaces, plus the extras it replaces)
local function specific_entries(char)
	local gt = game_table(char)
	local menu = gt and type(gt.menu) == "table" and gt.menu or {}
	local labels = gt and type(gt.labels) == "table" and gt.labels or {}
	local entries = {}
	for _, o0 in ipairs((catalog[char] or {}).body or {}) do
		local o = outfit_for(o0, char) or o0
		local direct = {}
		for _, v in ipairs(o.variants) do
			if o0.character == char then
				for _, c in ipairs(type(v.direct) == "table" and v.direct or {}) do direct[c] = true end
			else
				for c in pairs(v.costume_set or {}) do direct[c] = true end -- their costumes that use the file
			end
		end
		for _, fam in ipairs({ "normal", "alt", "extra" }) do
			if o.family_set[fam] then
				for _, c in ipairs(type(menu[fam]) == "table" and menu[fam] or {}) do
					if fam ~= "extra" or direct[c] then
						local lbl = labels[tostring(c)]
						entries[#entries + 1] = { id = o.id .. "|" .. c,
							name = (o.name or o.id) .. ((lbl and fam ~= "extra") and (" - " .. lbl) or "") }
					end
				end
			end
		end
	end
	return entries
end

local function draw_part(char, key, label, entries)
	if #entries == 0 then
		imgui.text(label .. ": (no outfits installed)")
		return
	end
	local c, n = combo(label .. "##co_" .. char .. "_" .. key, "Original (game)", entries, sel_of(char)[key])
	if c then set_choice(char, key, n, by_id[n or ""]) end
end

-- does the character have this part in any costume? (no game table: assume yes)
local function has_part(char, part)
	local gt = game_table(char)
	if not (gt and type(gt.costumes) == "table") then return true end
	for _, parts in pairs(gt.costumes) do
		if type(parts) == "table" and type(parts[part]) == "table" then return true end
	end
	return false
end

local function draw_character(char)
	local sel = sel_of(char)
	local fams = families_of(char)
	if fams.alt then
		draw_part(char, "body_normal", "Body - Normal", outfits_for(char, "body", "normal"))
		draw_part(char, "body_alt", "Body - Alternative", outfits_for(char, "body", "alt"))
	elseif fams.normal then
		draw_part(char, "body_normal", "Body", outfits_for(char, "body", "normal"))
	else
		draw_part(char, "body_normal", "Body", outfits_for(char, "body", nil))
	end
	if has_part(char, "face") then draw_part(char, "face", "Face", outfits_for(char, "face", nil)) end
	if has_part(char, "hair") then draw_part(char, "hair", "Hair", outfits_for(char, "hair", nil)) end
	-- specific costume: only for characters with an alternative costume (Claire, Leon, Sherry)
	if not fams.alt then return end
	imgui.separator()
	-- the box turns it on/off; the selector keeps the choice even when off
	local entries = specific_entries(char)
	local cc, on = imgui.checkbox("##co_spec_on_" .. char, sel.specific_on == true)
	if cc then
		if on and (type(sel.body_specific) ~= "string" or not sel.body_specific:match("|%d+$")) and entries[1] then
			sel.body_specific = entries[1].id
		end
		set_choice(char, "specific_on", on or nil, nil)
		local spec = specific_outfit(char)
		if spec then queue_outfit(spec) end
	end
	imgui.same_line()
	if #entries == 0 then
		imgui.text("Specific costume: (no outfits installed)")
	else
		local c, n = combo("Specific costume##co_spec_" .. char, nil, entries, sel.body_specific)
		if c then set_choice(char, "body_specific", n, by_id[(n or ""):match("^(.*)|") or ""]) end
	end
end

local function draw_content()
	if imgui.button("Rescan outfits") then
		set_status("outfits found: " .. scan())
	end
	if player_char then
		imgui.text(("Playing as: %s (game costume %s)"):format(CHAR_NAMES[player_char] or player_char, tostring(player_costume)))
	else
		imgui.text("Load a game to apply outfits.")
	end
	local function char_node(id)
		local where = seen_now[id]
		local title = (CHAR_NAMES[id] or id) .. (where == "npc" and "  (in scene as NPC)" or "")
			.. (where == "jogador" and "  (playing)" or "") .. "##co_char_" .. id
		if imgui.tree_node(title) then
			draw_character(id)
			imgui.tree_pop()
		end
	end
	for _, id in ipairs(CHAR_LIST) do char_node(id) end
	if imgui.tree_node("Other characters##co_others") then
		-- fixed ones + any other with a converted outfit or present in the scene
		local listed, others = {}, {}
		for _, id in ipairs(CHAR_LIST) do listed[id] = true end
		for _, id in ipairs(OTHER_BASE) do listed[id] = true; others[#others + 1] = id end
		local extra = {}
		for id in pairs(catalog) do if not listed[id] then listed[id] = true; extra[#extra + 1] = id end end
		for id in pairs(seen_now) do if not listed[id] then listed[id] = true; extra[#extra + 1] = id end end
		table.sort(extra)
		for _, id in ipairs(extra) do others[#others + 1] = id end
		for _, id in ipairs(others) do char_node(id) end
		imgui.tree_pop()
	end
	local pending = #preload_queue
	if pending > 0 then imgui.text(("loading outfits: %d variant(s) queued"):format(pending)) end
	for k, job in pairs(jobs) do
		if job.stage == "wait" then imgui.text("swapping " .. k .. "...") end
	end
	imgui.text("Status: " .. status)
	imgui.separator()
	if imgui.tree_node("Debug##co_debug") then
		draw_debug()
		imgui.tree_pop()
	end
end

re.on_draw_ui(function()
	if not imgui.tree_node("Custom Outfits") then return end
	if not scanned then scan() end
	if not settings.show_window then
		if imgui.button("Open in separate window") then settings.show_window = true; save() end
		draw_content()
	else
		imgui.text("Open in the \"Custom Outfits\" window.")
	end
	imgui.tree_pop()
end)

re.on_frame(function()
	frame = frame + 1
	-- separate window (like RE2R Classic: shows up together with the REFramework menu)
	if settings.show_window and reframework:is_drawing_ui() then
		if not scanned then scan() end
		if imgui.begin_window("Custom Outfits", true, 0) == false then
			settings.show_window = false
			save()
		else
			imgui.push_id(48213)
			local ok, err = pcall(draw_content)
			imgui.pop_id()
			imgui.end_window()
			if not ok then log_("menu error: " .. tostring(err)) end
		end
	end
	-- preloading: one variant per frame (spreads out the "hitch")
	local v = table.remove(preload_queue, 1)
	if v then
		local ok, err = pcall(load_now, v)
		queued[v.mdf] = nil
		if not ok then log_("preload failed: " .. tostring(err)) end
	end
	if next(jobs) == nil and not force_check and frame % CHECK_EVERY ~= 0 then return end
	local ok, err = pcall(tick)
	if not ok then log_("tick error: " .. tostring(err)) end
end)

set_status("CO_Outfits v17 loaded, outfits: " .. scan())
