-- Isaac Valley Bridge (Isaac side)
-- Every second:
--   1. reads what happened in Stardew: damage you took ("stardewDamage=N", in half hearts)
--      and bombs you used ("stardewBombs=N"), and applies the new part to this run;
--   2. writes all of Isaac's stats so Stardew can use them.
-- Everything goes through this mod's save file (save1.dat etc. in this mod's folder).

local mod = RegisterMod("IsaacValleyBridge", 1)

local lastLine = "bridge: waiting for a run..."
local appliedDamage = nil -- how much of Stardew's damage this run has already taken
local appliedBombs = nil  -- how many of Stardew's bombs this run has already paid for

local function round(x)
  return math.floor(x * 100 + 0.5) / 100
end

-- the totals Stardew has written into our file (or nil if it hasn't)
local function readFromStardew()
  if not Isaac.HasModData(mod) then
    return nil, nil
  end
  local text = Isaac.LoadModData(mod)
  local damage = string.match(text, "stardewDamage=(%d+)")
  local bombs = string.match(text, "stardewBombs=(%d+)")
  return damage and tonumber(damage), bombs and tonumber(bombs)
end

-- take half hearts off Isaac: soul/black hearts first, then red
local function hurt(player, halfHearts)
  for i = 1, halfHearts do
    if player:GetSoulHearts() > 0 then
      player:AddSoulHearts(-1)
    elseif player:GetHearts() > 0 then
      player:AddHearts(-1)
    end
  end
  SFXManager():Play(SoundEffect.SOUND_ISAAC_HURT_GRUNT, 1, 0, false, 1)
  if player:GetHearts() + player:GetSoulHearts() <= 0 then
    player:Die()
  end
end

-- items that change how Isaac attacks (weapons and multi-shot). Names from CollectibleType.
local WATCHED_ITEMS = {
  "BRIMSTONE", "TECHNOLOGY", "TECHNOLOGY_2", "MOMS_KNIFE", "DR_FETUS", "EPIC_FETUS", "TECH_X",
  "IPECAC", "THE_INNER_EYE", "MUTANT_SPIDER", "20_20", "THE_WIZ", "MOMS_EYE", "LOKIS_HORNS",
  "POLYPHEMUS", "CHOCOLATE_MILK", "MONSTROS_LUNG", "CURSED_EYE",
}

-- "BRIMSTONE:1,THE_INNER_EYE:2"
local function itemList(player)
  local out = {}
  for _, name in ipairs(WATCHED_ITEMS) do
    local id = CollectibleType["COLLECTIBLE_" .. name]
    if id ~= nil and player:HasCollectible(id) then
      out[#out + 1] = name .. ":" .. player:GetCollectibleNum(id)
    end
  end
  return table.concat(out, ",")
end

-- every active tear effect, straight from Isaac: "HOMING,PIERCING,POISON"
-- (this is how items like Spoon Bender or Cupid's Arrow change your tears)
local function flagList(player)
  local out = {}
  local flags = player.TearFlags
  for name, bit in pairs(TearFlags) do
    if math.type(bit) == "integer" and bit ~= 0 and (flags & bit) ~= 0 then
      out[#out + 1] = (string.gsub(name, "^TEAR_", ""))
    end
  end
  return table.concat(out, ",")
end

local function tearColor(player)
  local c = player.TearColor
  return round(c.R) .. ";" .. round(c.G) .. ";" .. round(c.B)
end

-- every item Isaac holds: "1:1,3:1,118:1" (Stardew uses this to draw each item's costume)
local function collectibleList(player)
  local out = {}
  -- how many items exist: Isaac's own count if this version has it, else a safe upper bound
  local count = 800
  local ok, n = pcall(function() return CollectibleType.NUM_COLLECTIBLES end)
  if ok and type(n) == "number" and n > 1 then count = n - 1 end

  for id = 1, count do
    -- item numbers have gaps; check each one on its own so one gap can't empty the list
    local fine, has = pcall(function() return player:HasCollectible(id) end)
    if fine and has then
      local num = 1
      pcall(function() num = player:GetCollectibleNum(id) end)
      out[#out + 1] = id .. ":" .. num
    end
  end
  return table.concat(out, ",")
end

-- Isaac's skin colors (body;head), e.g. black with some items
-- transformations (Guppy, Beelzebub, Bob, ...): Isaac numbers them 0..12
local function formList(player)
  local forms = {}
  for i = 0, 12 do
    local ok, has = pcall(function() return player:HasPlayerForm(i) end)
    if ok and has then forms[#forms + 1] = tostring(i) end
  end
  return table.concat(forms, ",")
end

local function skinColors(player)
  return player:GetBodyColor() .. ";" .. player:GetHeadColor()
end

local function laserColor(player)
  local c = player.LaserColor
  return round(c.R) .. ";" .. round(c.G) .. ";" .. round(c.B)
end

-- never let one of these break the whole bridge
local function safe(fn, player, fallback)
  local ok, result = pcall(fn, player)
  if ok then return result end
  return fallback
end

-- a new run (or continuing one): what happened in Stardew before doesn't count
function mod:onGameStarted(fromSave)
  local damage, bombs = readFromStardew()
  appliedDamage = damage or 0
  appliedBombs = bombs or 0
end

-- runs 30 times per second during a run
function mod:onUpdate()
  local game = Game()
  if game:GetFrameCount() % 30 ~= 0 then
    return -- only once per second
  end

  local player = Isaac.GetPlayer(0)
  if player == nil then
    return
  end

  -- 1. what happened in Stardew
  local damage, bombs = readFromStardew()
  if appliedDamage == nil then appliedDamage = damage or 0 end -- mod was reloaded mid-run
  if appliedBombs == nil then appliedBombs = bombs or 0 end

  if damage ~= nil then
    if damage > appliedDamage then
      hurt(player, damage - appliedDamage)
    end
    appliedDamage = damage
  end

  if bombs ~= nil then
    if bombs > appliedBombs then
      player:AddBombs(-(bombs - appliedBombs))
    end
    appliedBombs = bombs
  end

  -- 2. Isaac's stats for Stardew (simple "key=value" lines)
  local data = table.concat({
    "frame=" .. game:GetFrameCount(),
    "redHearts=" .. player:GetHearts(),       -- in half hearts
    "maxHearts=" .. player:GetMaxHearts(),    -- in half hearts
    "soulHearts=" .. player:GetSoulHearts(),  -- in half hearts
    "damage=" .. round(player.Damage),
    "fireDelay=" .. round(player.MaxFireDelay),
    "shotSpeed=" .. round(player.ShotSpeed),
    "moveSpeed=" .. round(player.MoveSpeed),
    "range=" .. round(-player.TearHeight),    -- Isaac shows range as a positive number
    "luck=" .. round(player.Luck),
    "coins=" .. player:GetNumCoins(),
    "bombs=" .. player:GetNumBombs(),
    "keys=" .. player:GetNumKeys(),
    "tearFlags=" .. safe(flagList, player, ""),
    "items=" .. safe(itemList, player, ""),
    "tearColor=" .. safe(tearColor, player, "1;1;1"),
    "laserColor=" .. safe(laserColor, player, ""),
    "collectibles=" .. safe(collectibleList, player, ""),
    "skin=" .. safe(skinColors, player, ""),
    "forms=" .. safe(formList, player, ""),
    "appliedDamage=" .. appliedDamage,
    "appliedBombs=" .. appliedBombs,
    "stardewDamage=" .. (damage or appliedDamage), -- keep Stardew's lines in the file
    "stardewBombs=" .. (bombs or appliedBombs),
  }, "\n")

  Isaac.SaveModData(mod, data)

  lastLine = "bridge: dmg " .. round(player.Damage)
    .. "  tears " .. round(player.MaxFireDelay)
    .. "  range " .. round(-player.TearHeight)
    .. "  stardew hits " .. appliedDamage
    .. "  bombs " .. appliedBombs
end

--[[ =========================================================================
  ENGINE MODE (the SkyCraft way)
  Isaac does the real work: Stardew tells Isaac which way you shoot, when you drop a bomb,
  where you stand and where the Stardew monsters are. Isaac's own engine fires your real
  tears / Brimstone / knife / bombs / familiars at invisible stand-ins placed where those
  monsters are, and we send back everything Isaac draws plus every hit.
  Needs the --luadebug launch option (so we can read and write files fast).
  ========================================================================= ]]

local K = 1 / 3 -- Isaac units per Stardew pixel (Stardew draws Isaac's sprites 3x)

local engine = {
  dir = nil,           -- this mod's folder (where the link files live)
  active = false,      -- is Stardew linked right now?
  lastSeq = -1,
  staleFrames = 0,
  shootAction = -1,    -- ButtonAction to hold down, or -1
  bombsSeen = nil,     -- Stardew's bomb-press counter
  bombFrames = 0,      -- frames left to hold the bomb button
  px = 0, py = 0,      -- Stardew player position (pixels)
  ax = nil, ay = nil,  -- anchor: the Stardew spot that maps to the room's center
  monsters = {},       -- id -> {x, y, r} in Stardew pixels
  kills = {},          -- tear indexes Stardew wants removed (they hit a Stardew wall)
  animNames = {},      -- sprite file -> list of animation names (sent by Stardew)
  standins = {},       -- id -> Isaac entity standing in for a Stardew monster
  hits = {},           -- id -> total damage Isaac did to that stand-in
  status = "engine: off",
  chargeFrames = 0,    -- how long Isaac has been charging Brimstone (in Isaac frames)
}

-- find this mod's folder (works with --luadebug)
local function findModFolder()
  if io == nil or io.open == nil then
    return nil
  end
  if debug ~= nil and debug.getinfo ~= nil then
    local src = debug.getinfo(1, "S").source or ""
    if src:sub(1, 1) == "@" then src = src:sub(2) end
    local dir = src:match("^(.*[/\\])")
    if dir ~= nil and (dir:sub(1, 1) == "/" or dir:match("^%a:")) then
      return dir
    end
  end
  if os == nil or os.getenv == nil then return nil end
  local profile = os.getenv("USERPROFILE")
  if profile ~= nil then -- Windows
    return profile .. "/Documents/My Games/Binding of Isaac Afterbirth+ Mods/IsaacValleyBridge/"
  end
  local home = os.getenv("HOME")
  if home ~= nil then
    local mac = home .. "/Library/Application Support/Binding of Isaac Afterbirth+ Mods/IsaacValleyBridge/"
    local f = io.open(mac .. "main.lua", "r")
    if f ~= nil then f:close() return mac end
    return home .. "/.local/share/binding of isaac afterbirth+ mods/IsaacValleyBridge/" -- Linux
  end
  return nil
end

engine.dir = findModFolder()
if engine.dir == nil then
  engine.status = "engine: add --luadebug to Isaac's launch options"
end

local function toIsaac(sx, sy)
  local center = Game():GetRoom():GetCenterPos()
  return Vector(center.X + (sx - engine.ax) * K, center.Y + (sy - engine.ay) * K)
end

local function toStardewX(v) return engine.ax + (v.X - Game():GetRoom():GetCenterPos().X) / K end
local function toStardewY(v) return engine.ay + (v.Y - Game():GetRoom():GetCenterPos().Y) / K end

local function readLinkFile()
  local f = io.open(engine.dir .. "link_in.txt", "rb")
  if f == nil then return nil end
  local text = f:read("*a")
  f:close()
  -- some programs put an invisible marker (BOM) at the start of a text file; ignore it
  return (text:gsub("^\239\187\191", ""))
end

local function writeLinkFile(text)
  local path = engine.dir .. "link_out.txt"
  local tmp = path .. ".tmp"
  local f = io.open(tmp, "wb") -- "b": no Windows line endings
  if f == nil then return end
  f:write(text)
  f:close()
  if os ~= nil and os.rename ~= nil then
    -- Windows can't rename over a file, so remove it first; if Stardew is reading it right now,
    -- this fails and we just try again next frame
    os.remove(path)
    os.rename(tmp, path)
  end
end

-- Stardew direction (0 up, 1 right, 2 down, 3 left) -> Isaac's shoot button
local SHOOT_ACTIONS = { [0] = ButtonAction.ACTION_SHOOTUP, [1] = ButtonAction.ACTION_SHOOTRIGHT,
                        [2] = ButtonAction.ACTION_SHOOTDOWN, [3] = ButtonAction.ACTION_SHOOTLEFT }

-- shift Isaac's flying things when Stardew moves the anchor, so they stay put in Stardew
local SHIFTED_TYPES = { [EntityType.ENTITY_TEAR] = true, [EntityType.ENTITY_BOMBDROP] = true,
                        [EntityType.ENTITY_EFFECT] = true, [EntityType.ENTITY_KNIFE] = true }

local function readInput()
  local text = readLinkFile()
  if text == nil then return false end

  local v = {}
  local monsters, kills, anims = {}, {}, {}
  for line in string.gmatch(text, "[^\n]+") do
    local key, val = line:match("^([%w_]+)=(.*)$")
    if key == "m" then
      local id, x, y, r = val:match("^(%-?%d+)|([%-%d%.]+)|([%-%d%.]+)|([%-%d%.]+)$")
      if id ~= nil then
        monsters[tonumber(id)] = { x = tonumber(x), y = tonumber(y), r = tonumber(r) }
      end
    elseif key == "kill" then
      kills[tonumber(val)] = true
    elseif key == "anim" then
      local file, names = val:match("^(.-)|(.*)$")
      if file ~= nil then
        local list = {}
        for n in string.gmatch(names, "[^,]+") do list[#list + 1] = n end
        anims[file] = list
      end
    elseif key ~= nil then
      v[key] = val
    end
  end

  local seq = tonumber(v.seq or "-1")
  if seq == engine.lastSeq then
    engine.staleFrames = engine.staleFrames + 1
  else
    engine.staleFrames = 0
    engine.lastSeq = seq
  end
  -- Stardew stopped writing (closed or paused): let go of the controls
  if engine.staleFrames > 45 or v.active ~= "1" then
    return false
  end

  engine.shootAction = SHOOT_ACTIONS[tonumber(v.shoot or "-1")] or -1
  local bombs = tonumber(v.bombs or "0")
  if engine.bombsSeen == nil then engine.bombsSeen = bombs end
  if bombs > engine.bombsSeen then
    engine.bombFrames = 2
    engine.bombsSeen = bombs
  end
  engine.px = tonumber(v.px or "0")
  engine.py = tonumber(v.py or "0")

  -- the anchor moved: shift tears & co. so they stay where they are in Stardew
  local ax, ay = tonumber(v.ax or "0"), tonumber(v.ay or "0")
  if engine.ax ~= nil and (ax ~= engine.ax or ay ~= engine.ay) then
    local shift = Vector((engine.ax - ax) * K, (engine.ay - ay) * K)
    for _, e in ipairs(Isaac.GetRoomEntities()) do
      if SHIFTED_TYPES[e.Type] then
        e.Position = e.Position + shift
      end
    end
  end
  engine.ax, engine.ay = ax, ay

  engine.monsters = monsters
  engine.kills = kills
  for file, list in pairs(anims) do engine.animNames[file] = list end
  return true
end

-- press Isaac's buttons for you (only while Stardew is linked)
function mod:onInputAction(entity, hook, action)
  if not engine.active or entity == nil or entity.Type ~= EntityType.ENTITY_PLAYER then
    return nil
  end

  -- Stardew moves you; Isaac shouldn't
  if action >= ButtonAction.ACTION_LEFT and action <= ButtonAction.ACTION_DOWN then
    if hook == InputHook.GET_ACTION_VALUE then return 0.0 end
    return false
  end

  if action >= ButtonAction.ACTION_SHOOTLEFT and action <= ButtonAction.ACTION_SHOOTDOWN then
    local on = (action == engine.shootAction)
    if hook == InputHook.GET_ACTION_VALUE then return on and 1.0 or 0.0 end
    return on
  end

  if action == ButtonAction.ACTION_BOMB and engine.bombFrames > 0 then
    if hook == InputHook.GET_ACTION_VALUE then return 1.0 end
    return true
  end
  return nil
end

-- keep Isaac standing exactly where you stand in Stardew
function mod:onPlayerUpdate(player)
  if not engine.active or engine.ax == nil then return end
  local room = Game():GetRoom()
  player.Position = room:GetClampedPosition(toIsaac(engine.px, engine.py), 10)
  player.Velocity = Vector(0, 0)
end

-- invisible stand-ins where the Stardew monsters are; your real attacks hit these
local function updateStandins()
  local seen = {}
  for id, m in pairs(engine.monsters) do
    local e = engine.standins[id]
    if e == nil or not e:Exists() then
      e = Isaac.Spawn(EntityType.ENTITY_GAPER, 0, 0, toIsaac(m.x, m.y), Vector(0, 0), nil)
      e.EntityCollisionClass = EntityCollisionClass.ENTCOLL_PLAYEROBJECTS -- only your attacks touch it
      e.GridCollisionClass = EntityGridCollisionClass.GRIDCOLL_NONE
      -- keep it "visible" so homing tears and other targeting see it, but fully transparent
      e.Visible = true
      e.CollisionDamage = 0
      e.MaxHitPoints = 100000
      e:AddEntityFlags(EntityFlag.FLAG_NO_KNOCKBACK | EntityFlag.FLAG_NO_PHYSICS_KNOCKBACK
        | EntityFlag.FLAG_NO_BLOOD_SPLASH | EntityFlag.FLAG_NO_DEATH_TRIGGER | EntityFlag.FLAG_HIDE_HP_BAR)
      pcall(function() e:ToNPC().CanShutDoors = false end) -- don't lock the room's doors
      e:SetSize(math.max(4, m.r * K), Vector(1, 1), 12)
      e:GetData().ivId = id
      engine.standins[id] = e
    end
    e.Position = toIsaac(m.x, m.y)
    e.Velocity = Vector(0, 0)
    e.HitPoints = e.MaxHitPoints -- never dies in Isaac; Stardew decides
    if e.FrameCount % 60 == 0 then
      -- the color class is called Color in game (KColor in the docs); try both
      local ok = pcall(function() e:SetColor(Color(1, 1, 1, 0, 0, 0, 0), 120, 99, false, false) end)
      if not ok then pcall(function() e:SetColor(KColor(1, 1, 1, 0, 0, 0, 0), 120, 99, false, false) end) end
    end
    seen[id] = true
  end
  for id, e in pairs(engine.standins) do
    if not seen[id] then
      if e:Exists() then e:Remove() end
      engine.standins[id] = nil
    end
  end
end

-- record every hit on a stand-in; while linked, only your own explosions can hurt Isaac here
function mod:onTakeDamage(entity, amount, flags, source, countdown)
  if not engine.active then return nil end
  local id = entity:GetData().ivId
  if id ~= nil then
    -- explosions are applied by Stardew itself (they also break rocks there)
    if flags & DamageFlag.DAMAGE_EXPLOSION == 0 then
      engine.hits[id] = (engine.hits[id] or 0) + amount
    end
    return nil
  end
  if entity.Type == EntityType.ENTITY_PLAYER and flags & DamageFlag.DAMAGE_EXPLOSION == 0 then
    return false
  end
  return nil
end

-- which animation is this sprite playing? (Stardew sends the names in each file)
local function currentAnim(sprite, file)
  local names = engine.animNames[file]
  if names == nil then return "" end
  for _, n in ipairs(names) do
    if sprite:IsPlaying(n) then return n end
  end
  for _, n in ipairs(names) do
    if sprite:IsFinished(n) then return n end
  end
  return ""
end

local STATUS_FLAGS = { "FLAG_FREEZE", "FLAG_POISON", "FLAG_SLOW", "FLAG_CHARM", "FLAG_CONFUSION",
                       "FLAG_MIDAS_FREEZE", "FLAG_FEAR", "FLAG_BURN", "FLAG_SHRINK" }

local DRAWN_TYPES = { [EntityType.ENTITY_TEAR] = true, [EntityType.ENTITY_FAMILIAR] = true,
                      [EntityType.ENTITY_BOMBDROP] = true, [EntityType.ENTITY_LASER] = true,
                      [EntityType.ENTITY_KNIFE] = true, [EntityType.ENTITY_EFFECT] = true }

local FROM_PLAYER = { [EntityType.ENTITY_PLAYER] = true, [EntityType.ENTITY_TEAR] = true,
                      [EntityType.ENTITY_FAMILIAR] = true, [EntityType.ENTITY_BOMBDROP] = true,
                      [EntityType.ENTITY_LASER] = true, [EntityType.ENTITY_KNIFE] = true }

local function f2(x) return string.format("%.2f", x) end

-- one line per thing Isaac is drawing that Stardew should draw too
-- read something from Isaac, or use a fallback if this version of the game doesn't have it
local function get(fn, fallback)
  local ok, value = pcall(fn)
  if ok and value ~= nil then return value end
  return fallback
end

local WHITE = { R = 1, G = 1, B = 1, A = 1, RO = 0, GO = 0, BO = 0 }

-- Isaac's bomb file (004.0XX_MultiBomb) holds every bomb look at once and shows only some layers.
-- Pick the same ones from the bomb's flags: the body for its shape, plus fire/tears/bolt/key/sparkles.
local function flagOn(flags, name)
  local ok, on = pcall(function() return TearFlags[name] ~= nil and flags & TearFlags[name] ~= 0 end)
  return ok and on
end

local function bombLayers(e)
  local bomb = e:ToBomb()
  if bomb == nil then return "" end
  local flags = get(function() return bomb.Flags end, 0)
  local v = e.Variant
  local big = (v == 1 or v == 6)
  local poop = v == 9 or flagOn(flags, "TEAR_BUTT_BOMB")
  local sad = v == 7 or flagOn(flags, "TEAR_SAD_BOMB")
  local bob = v == 5 or v == 6 or flagOn(flags, "TEAR_POISON")
  local hot = v == 8 or flagOn(flags, "TEAR_BURN")
  local glitter = v == 12 or flagOn(flags, "TEAR_GLITTER_BOMB")
  local homing = v == 11 or flagOn(flags, "TEAR_HOMING")
  local fast = get(function() return Isaac.GetPlayer(0):HasCollectible(517) end, false)

  -- shape order in the file's overlay layers: bob, poop, sad, normal (small, big each)
  local shape, body
  if poop then shape, body = 1, (big and 5 or 4)
  elseif bob then shape, body = 0, (big and 3 or 2)
  elseif sad and glitter then shape, body = 2, (big and 15 or 14)
  elseif sad then shape, body = 2, (big and 9 or 8)
  elseif glitter then shape, body = 3, (big and 13 or 12)
  elseif fast then shape, body = 3, (big and 19 or 18)
  else shape, body = 3, (big and 1 or 0) end

  local n = shape * 2 + (big and 2 or 1)        -- 1..8
  local layers = { string.format("body_%02d", body) }
  if hot then layers[#layers + 1] = string.format("fire_%02d", n) end
  if sad and shape ~= 3 then layers[#layers + 1] = string.format("tear_%02d", n) end
  if fast and body ~= 18 and body ~= 19 then
    local ray = shape == 3 and (big and 8 or 7) or n   -- normal shape: the glitter (pink) rays
    layers[#layers + 1] = string.format("ray_%02d", ray)
  end
  if homing then layers[#layers + 1] = string.format("key_%02d", n) end
  if glitter then layers[#layers + 1] = big and "spark_02" or "spark_01" end
  return table.concat(layers, ",")
end

-- one line per thing Isaac is drawing that Stardew should draw too
local function describe(e)
  local sprite = e:GetSprite()
  local file = get(function() return sprite:GetFilename() end, "")
  if file == "" then return nil end

  local height = get(function() return e.PositionOffset.Y end, 0) + get(function() return sprite.Offset.Y end, 0)
  local tearScale = 0
  local tear = e:ToTear()
  if tear ~= nil then
    -- a tear's height IS its offset above the ground (don't count it twice)
    height = tear.Height + get(function() return sprite.Offset.Y end, 0)
    tearScale = tear.Scale
  end

  local c = get(function() return e:GetColor() end, WHITE)
  local sc = get(function() return sprite.Color end, WHITE)
  local scaleX = get(function() return e.SpriteScale.X end, 1) * get(function() return sprite.Scale.X end, 1)
  local scaleY = get(function() return e.SpriteScale.Y end, 1) * get(function() return sprite.Scale.Y end, 1)
  local rotation = get(function() return sprite.Rotation end, 0) + get(function() return e.SpriteRotation end, 0)
  local flip = (get(function() return sprite.FlipX end, false) or get(function() return e.FlipX end, false)) and 1 or 0

  local endX, endY = "", ""
  local laser = e:ToLaser()
  if laser ~= nil then
    local p = get(function() return laser:GetEndPoint() end, nil)
    if p ~= nil then
      endX, endY = f2(toStardewX(p)), f2(toStardewY(p))
    end
  end

  return table.concat({
    e.Type, e.Variant, e.SubType, e.Index,
    f2(toStardewX(e.Position)), f2(toStardewY(e.Position)), f2(height / K),
    file, currentAnim(sprite, file), get(function() return sprite:GetFrame() end, 0),
    f2(rotation), f2(scaleX), f2(scaleY), flip,
    f2(c.R * sc.R), f2(c.G * sc.G), f2(c.B * sc.B), f2(c.A * sc.A),
    f2(c.RO + sc.RO), f2(c.GO + sc.GO), f2(c.BO + sc.BO),
    f2(tearScale), endX, endY,
    (string.find(string.lower(file), "multibomb", 1, true) and bombLayers(e) or ""),
  }, "|")
end

local function engineUpdate()
  if engine.dir == nil then return end

  engine.active = readInput()
  if not engine.active then
    engine.status = "engine: waiting for Stardew"
    for id, e in pairs(engine.standins) do
      if e:Exists() then e:Remove() end
    end
    engine.standins = {}
    return
  end

  if engine.bombFrames > 0 then engine.bombFrames = engine.bombFrames - 1 end
  updateStandins()

  -- tears that hit a wall in Stardew splash, like they hit a wall in Isaac
  local room = Game():GetRoom()
  local out = { "seq=" .. engine.lastSeq, "frame=" .. Game():GetFrameCount() }
  local tl, br = room:GetTopLeftPos(), room:GetBottomRightPos()
  out[#out + 1] = "roomHalf=" .. f2((br.X - tl.X) / 2 / K) .. "|" .. f2((br.Y - tl.Y) / 2 / K)

  local count = 0
  for _, e in ipairs(Isaac.GetRoomEntities()) do
    if e.Type == EntityType.ENTITY_TEAR and engine.kills[e.Index] then
      e:Die()
    elseif DRAWN_TYPES[e.Type] and e.Visible and e:GetData().ivId == nil then
      -- effects: the ones your things make, plus fresh unclaimed ones (Isaac doesn't mark
      -- bomb explosions as yours); old leftovers like scorch marks stay in Isaac
      local fromPlayer = e.Type ~= EntityType.ENTITY_EFFECT or FROM_PLAYER[e.SpawnerType]
        or ((e.SpawnerType == nil or e.SpawnerType == 0) and e.FrameCount < 60)
      if fromPlayer then
        local ok, line = pcall(describe, e)
        if ok and line ~= nil then
          out[#out + 1] = "e=" .. line
          count = count + 1
        end
      end
    end
  end

  -- hits and status effects on the stand-ins
  for id, total in pairs(engine.hits) do
    if engine.standins[id] ~= nil then
      out[#out + 1] = "hit=" .. id .. "|" .. f2(total)
    end
  end
  for id, e in pairs(engine.standins) do
    if e:Exists() then
      local names = {}
      for _, name in ipairs(STATUS_FLAGS) do
        local bit = EntityFlag[name]
        if bit ~= nil and e:HasEntityFlags(bit) then
          names[#names + 1] = (string.gsub(name, "^FLAG_", ""))
        end
      end
      if #names > 0 then
        out[#out + 1] = "status=" .. id .. "|" .. table.concat(names, ",")
      end
    end
  end

  local player = Isaac.GetPlayer(0)
  if player ~= nil then
    out[#out + 1] = "headDir=" .. player:GetHeadDirection()

    -- the charge bar: Mom's Knife tells us its charge; Brimstone we count here, frame by frame
    pcall(function()
      local knifeCharge = nil
      for _, k in ipairs(Isaac.FindByType(EntityType.ENTITY_KNIFE, -1, -1, false, false)) do
        local knife = k:ToKnife()
        if knife ~= nil and not knife:IsFlying() then knifeCharge = knife.Charge end
      end
      if knifeCharge ~= nil and player:HasWeaponType(WeaponType.WEAPON_KNIFE) then
        out[#out + 1] = "charge=knife|" .. f2(knifeCharge) .. "|1"
      elseif player:HasWeaponType(WeaponType.WEAPON_BRIMSTONE) then
        local firing = #Isaac.FindByType(EntityType.ENTITY_LASER, 1, -1, false, false) > 0
        if engine.shootAction ~= -1 and not firing then
          engine.chargeFrames = engine.chargeFrames + 1
        else
          engine.chargeFrames = 0
        end
        out[#out + 1] = "charge=brimstone|" .. engine.chargeFrames .. "|" .. math.max(1, player.MaxFireDelay)
      else
        engine.chargeFrames = 0
      end
    end)
    local okp, line = pcall(function()
      local sprite = player:GetSprite()
      local file = sprite:GetFilename()
      local body, head = "", ""
      local names = engine.animNames[file]
      if names ~= nil then
        for _, n in ipairs(names) do
          if body == "" and (sprite:IsPlaying(n) or sprite:IsFinished(n)) then body = n end
          if head == "" and (sprite:IsOverlayPlaying(n) or sprite:IsOverlayFinished(n)) then head = n end
        end
      end
      local c = get(function() return player:GetColor() end, WHITE)
      return table.concat({ file, body, sprite:GetFrame(), head, sprite:GetOverlayFrame(),
        f2(c.R), f2(c.G), f2(c.B), f2(c.A), f2(c.RO), f2(c.GO), f2(c.BO),
        player.FireDelay, player.MaxFireDelay, player.HeadFrameDelay }, "|")
    end)
    if okp then out[#out + 1] = "player=" .. line end
  end

  writeLinkFile(table.concat(out, "\n") .. "\n")
  local monsters = 0
  for _ in pairs(engine.standins) do monsters = monsters + 1 end
  engine.status = "engine: linked, " .. count .. " things, " .. monsters .. " monsters"
end

function mod:onEngineUpdate()
  local ok, err = pcall(engineUpdate)
  if not ok then
    engine.status = "engine error: " .. tostring(err)
    Isaac.DebugString("Isaac Valley engine error: " .. tostring(err))
  end
end

function mod:onNewRoom()
  engine.standins = {}
  engine.hits = {}
end

-- draws the status lines near the bottom of the screen
function mod:onRender()
  Isaac.RenderText(lastLine, 60, 250, 1, 1, 1, 0.8)
  Isaac.RenderText(engine.status, 60, 236, 1, 1, 1, 0.8)
end

mod:AddCallback(ModCallbacks.MC_POST_GAME_STARTED, mod.onGameStarted)
mod:AddCallback(ModCallbacks.MC_POST_UPDATE, mod.onUpdate)
mod:AddCallback(ModCallbacks.MC_POST_UPDATE, mod.onEngineUpdate)
mod:AddCallback(ModCallbacks.MC_POST_RENDER, mod.onRender)
mod:AddCallback(ModCallbacks.MC_INPUT_ACTION, mod.onInputAction)
mod:AddCallback(ModCallbacks.MC_POST_PEFFECT_UPDATE, mod.onPlayerUpdate)
mod:AddCallback(ModCallbacks.MC_ENTITY_TAKE_DMG, mod.onTakeDamage)
mod:AddCallback(ModCallbacks.MC_POST_NEW_ROOM, mod.onNewRoom)

Isaac.DebugString("Isaac Valley Bridge loaded")
