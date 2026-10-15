const NS = "http://www.w3.org/2000/svg";
const regions = new Map();
const state = {
  singleGame: null,
  multiplayer: false,
  host: false,
  roomCode: "",
  socket: null,
  syncTimer: null,
  singlePollTimer: null,
  pollPending: false,
  playerName: "",
  inviteRoom: ""
};
state.online = false;
state.gameMode = "classic";
state.battleRevision = -1;
state.multiplayerFinished = false;

const elements = Object.fromEntries([
  "homePanel", "lobbyPanel", "gamePanel", "endPanel", "playerName",
  "singleRegion", "singleMode", "singleTimeOptions", "singleLivesOptions",
  "singleMinutes", "singleLives", "hostRegion", "hostMinutes", "roomCodeInput",
  "hostAddressInput", "serverAddress", "networkAddress", "invitationHint", "downloadDiagnostics",
  "startSingle", "createRoom", "joinRoom", "singleError", "multiplayerError",
  "lobbyError", "roomCodeDisplay", "inviteSection", "inviteLink", "copyInvite",
  "playerList", "lobbyStatus", "hostStartOptions", "startMultiplayer", "leaveLobby",
  "gameModeLabel", "gameTitle", "timerBlock", "timer", "livesBlock", "livesDisplay",
  "inviteGameBanner", "map", "mapHint", "guessForm", "guessInput", "guessFeedback",
  "zoomIn", "zoomOut", "zoomReset", "endGame",
  "endTitle", "endSummary", "leaderboardWrap", "leaderboard", "newGame",
  "liveLeaderboardWrap", "liveLeaderboard", "hostGameMode", "battleOptions", "hostBattleVariant"
].map(id => [id, document.getElementById(id)]));

let currentRegion = null;
let currentMode = "time";
let solvedIds = new Set();
let mapBaseView = { x: 0, y: 0, width: 1000, height: 680 };
let mapDrag = null;

async function initialize() {
  elements.startSingle.disabled = elements.createRoom.disabled = elements.joinRoom.disabled = true;
  recordDiagnostic("maps-loading");
  try {
    const hostingResponse = await fetch("/api/hosting");
    if (!hostingResponse.ok) throw new Error("Nastavenie servera sa nepodarilo načítať. Obnov stránku.");
    const hosting = await hostingResponse.json();
    configureHosting(hosting.mode);
    const response = await fetch("/data/map-shapes.json");
    recordDiagnostic("maps-http", { status: response.status });
    if (!response.ok) throw new Error("Mapy sa nepodarilo načítať.");
    const data = await response.json();
    for (const region of data.regions) regions.set(region.id, region);
    recordDiagnostic("maps-loaded", { regions: data.regions.map(region => region.id) });
    populateRegionSelect(elements.singleRegion, true);
    populateRegionSelect(elements.hostRegion, true);
    elements.startSingle.disabled = elements.createRoom.disabled = elements.joinRoom.disabled = false;
    const roomCode = new URLSearchParams(location.search).get("room");
    if (roomCode) {
      elements.roomCodeInput.value = roomCode.toUpperCase();
      elements.invitationHint.textContent = `Pozvánka do hry ${roomCode.toUpperCase()}: zadaj svoje meno a klikni na Pripojiť.`;
      elements.invitationHint.hidden = false;
      elements.playerName.focus();
    }
  } catch (error) {
    recordDiagnostic("maps-failed", { message: error.message, stack: error.stack });
    showError(elements.singleError, error.message || "Mapy sa nepodarilo načítať.");
    elements.startSingle.disabled = true;
    elements.createRoom.disabled = true;
    elements.joinRoom.disabled = true;
    showError(elements.multiplayerError, error.message);
  }
}

function configureHosting(mode) {
  state.online = mode === "internet";
  for (const id of ["lanJoinOptions", "lanNetworkOptions", "lanServerOptions"]) {
    document.getElementById(id).hidden = state.online;
  }
  document.getElementById("hostingBadge").textContent = state.online ? "Online · HTTPS" : "Lokálna hra · LAN";
  document.getElementById("multiplayerCopy").textContent = state.online
    ? "Vytvor hru a pošli kamarátom webový odkaz. Stačí prehliadač a internet; rovnaká Wi-Fi ani stiahnutie hry nie sú potrebné."
    : "Vytvor hru a pozvi hráčov cez webový odkaz alebo adresu hostiteľa a ID. Všetci musíte byť v rovnakej Wi-Fi/LAN sieti.";
  document.getElementById("lobbyMode").textContent = state.online ? "MULTIPLAYER · ONLINE" : "MULTIPLAYER · ROVNAKÁ SIEŤ";
}

function populateRegionSelect(select, includeRandom) {
  select.replaceChildren();
  if (includeRandom) {
    const randomOption = document.createElement("option");
    randomOption.value = "random";
    randomOption.textContent = "Náhodný región";
    select.append(randomOption);
  }
  for (const region of regions.values()) {
    const option = document.createElement("option");
    option.value = region.id;
    option.textContent = region.name;
    select.append(option);
  }
}

function showError(element, text) {
  element.textContent = text || "";
  if (text) recordDiagnostic("ui-message", { element: element.id, message: text });
}

function recordDiagnostic(event, detail = {}) {
  window.mapGameDiagnostics?.record(event, detail);
}

function setScreen(screen) {
  elements.homePanel.hidden = screen !== "home";
  elements.lobbyPanel.hidden = screen !== "lobby";
  elements.gamePanel.hidden = screen !== "game";
  elements.endPanel.hidden = screen !== "end";
}

function showFeedback(text, kind = "") {
  elements.guessFeedback.textContent = text;
  elements.guessFeedback.className = `feedback ${kind}`;
}

function formatTime(seconds) {
  const value = Math.max(0, Math.ceil(seconds));
  return `${String(Math.floor(value / 60)).padStart(2, "0")}:${String(value % 60).padStart(2, "0")}`;
}

function renderGame(regionId, mode, found = []) {
  currentRegion = regions.get(regionId);
  if (!currentRegion) throw new Error("Mapa tejto hry nie je dostupná. Použi aktuálny balík hry alebo webový odkaz hostiteľa.");

  currentMode = mode;
  solvedIds = new Set(found);
  elements.gameTitle.textContent = currentRegion.name;
  const battle = state.multiplayer && state.gameMode !== "classic";
  elements.gameModeLabel.textContent = battle
    ? `MAP BATTLE · ${state.gameMode === "battle-hardcore" ? "HARDCORE" : "NORMAL"}`
    : state.multiplayer ? "MULTIPLAYER · SÚŤAŽ O ČAS" : mode === "time" ? "HRA NA ČAS" : "HRA NA ŽIVOTY";
  elements.timerBlock.hidden = mode !== "time";
  elements.livesBlock.hidden = mode !== "lives";
  elements.liveLeaderboardWrap.hidden = !state.multiplayer;
  elements.liveLeaderboard.replaceChildren();
  elements.mapHint.textContent = battle ? "Spoločná mapa · farby vlastníkov sú v rebríčku" : "Uhádnuté štáty sa vyfarbia nazeleno";
  elements.endGame.disabled = false;
  elements.guessInput.disabled = false;
  elements.endGame.textContent = state.multiplayer && !state.host ? "Opustiť hru" : "Ukončiť hru";
  renderMap(currentRegion, solvedIds);
  elements.guessInput.value = "";
  showFeedback("");
  elements.gamePanel.hidden = false;
  elements.guessInput.focus();
}

function project([longitude, latitude]) {
  const latitudeRadians = Math.max(-82, Math.min(82, latitude)) * Math.PI / 180;
  return [
    longitude * Math.PI / 180,
    Math.log(Math.tan(Math.PI / 4 + latitudeRadians / 2))
  ];
}

function getPolygons(geometry) {
  return geometry.type === "Polygon"
    ? [geometry.coordinates]
    : geometry.type === "MultiPolygon"
      ? geometry.coordinates
      : [];
}

function renderMap(region, found) {
  const svg = elements.map;
  svg.replaceChildren();
  const featurePoints = region.features.map(feature => {
    const polygons = getPolygons(feature.geometry).map(polygon => polygon.map(ring => ring.map(project)));
    return { feature, polygons };
  });
  const allPoints = featurePoints.flatMap(entry =>
    entry.polygons.flatMap(polygon => polygon.flatMap(ring => ring)));
  const minX = Math.min(...allPoints.map(point => point[0]));
  const maxX = Math.max(...allPoints.map(point => point[0]));
  const minY = Math.min(...allPoints.map(point => point[1]));
  const maxY = Math.max(...allPoints.map(point => point[1]));
  const width = 1000;
  const height = 680;
  const padding = 24;
  const scale = Math.min((width - padding * 2) / Math.max(maxX - minX, 0.01), (height - padding * 2) / Math.max(maxY - minY, 0.01));
  const offsetX = (width - (maxX - minX) * scale) / 2;
  const offsetY = (height - (maxY - minY) * scale) / 2;
  const place = ([x, y]) => [offsetX + (x - minX) * scale, height - offsetY - (y - minY) * scale];
  mapBaseView = { x: 0, y: 0, width, height };
  setMapView(mapBaseView);

  for (const { feature, polygons } of featurePoints) {
    let pathData = "";
    for (const polygon of polygons) {
      const projectedRings = polygon.map(ring => ring.map(place));
      for (const ring of projectedRings) {
        if (ring.length < 3) continue;
        pathData += `M${ring.map(point => `${point[0].toFixed(2)},${point[1].toFixed(2)}`).join("L")}Z`;
      }
    }

    const path = document.createElementNS(NS, "path");
    path.setAttribute("d", pathData);
    path.setAttribute("class", `country-shape${found.has(feature.id) ? " solved" : ""}`);
    path.setAttribute("fill-rule", "evenodd");
    path.dataset.id = feature.id;
    svg.append(path);
  }
}

function setMapView(view) {
  const svg = elements.map;
  const width = Math.min(mapBaseView.width, Math.max(mapBaseView.width / 8, view.width));
  const height = width * mapBaseView.height / mapBaseView.width;
  const x = Math.max(mapBaseView.x, Math.min(mapBaseView.x + mapBaseView.width - width, view.x));
  const y = Math.max(mapBaseView.y, Math.min(mapBaseView.y + mapBaseView.height - height, view.y));
  const bounded = { x, y, width, height };
  svg.setAttribute("viewBox", `${bounded.x} ${bounded.y} ${bounded.width} ${bounded.height}`);
  elements.zoomReset.textContent = `${Math.round(mapBaseView.width / bounded.width * 100)}%`;
}

function zoomMap(factor, position = null) {
  const values = elements.map.getAttribute("viewBox").split(/\s+/).map(Number);
  const [x, y, width, height] = values;
  const bounds = elements.map.getBoundingClientRect();
  const pointX = position
    ? x + (position.x - bounds.left) / bounds.width * width
    : x + width / 2;
  const pointY = position
    ? y + (position.y - bounds.top) / bounds.height * height
    : y + height / 2;
  const newWidth = Math.min(mapBaseView.width, Math.max(mapBaseView.width / 8, width * factor));
  const ratio = newWidth / width;
  setMapView({
    x: pointX - (pointX - x) * ratio,
    y: pointY - (pointY - y) * ratio,
    width: newWidth
  });
}

function updateSolved(found) {
  solvedIds = new Set(found);
  for (const shape of elements.map.querySelectorAll("[data-id]")) {
    shape.classList.toggle("solved", solvedIds.has(shape.dataset.id));
  }
}

function updateMultiplayerProgress(message) {
  if (state.gameMode === "classic") {
    if (message.foundIds) updateSolved(message.foundIds);
  } else {
    if (message.revision < state.battleRevision) return;
    state.battleRevision = message.revision;
    const ownership = new Map(message.ownership.map(country => [country.id, country.color]));
    for (const shape of elements.map.querySelectorAll("[data-id]")) {
      shape.classList.toggle("solved", false);
      const color = ownership.get(shape.dataset.id);
      if (color) shape.style.fill = color;
      else shape.style.removeProperty("fill");
    }
  }
  if (message.leaderboard) renderLeaderboard(elements.liveLeaderboard, message.leaderboard);
}

function finishSingleGame(game) {
  clearInterval(state.singlePollTimer);
  state.singlePollTimer = null;
  elements.timer.textContent = "00:00";
  updateSolved(game.foundIds);
  const guessed = game.guessedCount ?? 0;
  const total = game.totalCount ?? currentRegion.features.length;
  const reason = {
    time: "Vypršal čas.",
    lives: "Prišiel si o všetky životy.",
    completed: "Uhádol si všetky štáty!",
    manual: "Hru si ukončil."
  }[game.endReason] || "Hra sa skončila.";
  elements.endTitle.textContent = game.endReason === "completed" ? "Skvelá práca!" : "Koniec hry";
  elements.endSummary.textContent = `${reason} Uhádol si ${guessed} z ${total} štátov v regióne ${game.regionName}.`;
  elements.leaderboardWrap.hidden = true;
  setScreen("end");
}

function applySingleGame(game) {
  updateSolved(game.foundIds);
  if (game.remainingSeconds !== null) elements.timer.textContent = formatTime(game.remainingSeconds);
  if (game.livesRemaining !== null) {
    elements.livesDisplay.textContent = "♥".repeat(game.livesRemaining) + "♡".repeat(5 - game.livesRemaining);
  }
  if (game.status === "finished") finishSingleGame(game);
}

async function startSingleGame() {
  showError(elements.singleError, "");
  const request = {
    regionId: elements.singleRegion.value,
    mode: elements.singleMode.value,
    timeLimitSeconds: Number(elements.singleMinutes.value) * 60,
    lives: Number(elements.singleLives.value)
  };

  try {
    const response = await fetch("/api/games", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request)
    });
    const game = await response.json();
    if (!response.ok) throw new Error(game.error || "Hru sa nepodarilo spustiť.");
    state.multiplayer = false;
    state.singleGame = game;
    renderGame(game.regionId, game.mode, game.foundIds);
    applySingleGame(game);
    if (game.status !== "finished") {
      state.singlePollTimer = setInterval(pollSingleGame, 1000);
    }
  } catch (error) {
    showError(elements.singleError, error.message || "Hru sa nepodarilo spustiť.");
  }
}

async function pollSingleGame() {
  if (!state.singleGame || state.pollPending) return;
  state.pollPending = true;
  try {
    const response = await fetch(`/api/games/${state.singleGame.id}`);
    if (!response.ok) throw new Error("Spojenie s hrou sa prerušilo.");
    applySingleGame(await response.json());
  } catch (error) {
    showFeedback(error.message, "error");
    clearInterval(state.singlePollTimer);
    state.singlePollTimer = null;
  } finally {
    state.pollPending = false;
  }
}

async function submitSingleGuess(guess) {
  const response = await fetch(`/api/games/${state.singleGame.id}/guess`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ guess })
  });
  const result = await response.json();
  if (!response.ok) throw new Error(result.error || "Tip sa nepodarilo odoslať.");
  applySingleGame(result.game);
  showFeedback(result.message, result.outcome === "correct" ? "success" : "error");
}

async function endSingleGame() {
  if (!state.singleGame || !window.confirm("Naozaj chceš ukončiť hru a zobraziť svoj výsledok?")) return;

  elements.endGame.disabled = true;
  try {
    const response = await fetch(`/api/games/${state.singleGame.id}/finish`, { method: "POST" });
    if (!response.ok) throw new Error("Hru sa nepodarilo ukončiť.");
    applySingleGame(await response.json());
  } catch (error) {
    showFeedback(error.message || "Hru sa nepodarilo ukončiť.", "error");
  } finally {
    elements.endGame.disabled = false;
  }
}

function connectMultiplayer(action) {
  showError(elements.multiplayerError, "");
  showError(elements.lobbyError, "");
  const name = elements.playerName.value.trim();
  if (!name || name.length > 24 || /[\u0000-\u001f\u007f]/.test(name)) {
    showError(elements.multiplayerError, "Zadaj svoje meno (1 až 24 znakov).");
    elements.playerName.focus();
    return;
  }
  if (state.socket && state.socket.readyState < WebSocket.CLOSING) return;
  const roomCode = elements.roomCodeInput.value.trim().toUpperCase();
  let serverOrigin = location.origin;
  if (action === "join" && !/^[A-Z0-9]{6}$/.test(roomCode)) {
    showError(elements.multiplayerError, "Zadaj platné šesťmiestne ID hry.");
    return;
  }

  if (!state.online && action === "join" && elements.hostAddressInput.value.trim()) {
    try {
      const input = elements.hostAddressInput.value.trim();
      const address = new URL(input.includes("://") ? input : `http://${input}`);
      if (!["http:", "https:"].includes(address.protocol) || address.username || address.password) {
        throw new Error();
      }
      if (!address.port && address.protocol === "http:") address.port = "5080";
      serverOrigin = address.origin;
    } catch {
      showError(elements.multiplayerError, "Zadaj platnú HTTP adresu hostiteľa, napríklad http://192.168.1.25:5080.");
      return;
    }
  } else if (!state.online && action === "join" && isLocalAddress(location.hostname)
      && !new URLSearchParams(location.search).has("room")) {
    showError(elements.multiplayerError, "V stiahnutej hre zadaj LAN adresu hostiteľa.");
    elements.hostAddressInput.focus();
    return;
  }

  state.host = action === "create";
  state.multiplayer = true;
  state.playerName = name;
  state.inviteRoom = "";
  elements.inviteSection.hidden = true;
  elements.roomCodeDisplay.textContent = "…";
  elements.playerList.replaceChildren();
  elements.hostStartOptions.hidden = true;
  elements.leaveLobby.textContent = "Opustiť čakáreň";
  elements.lobbyStatus.textContent = "Pripájam sa k hre…";
  const serverAddress = new URL(serverOrigin);
  const scheme = serverAddress.protocol === "https:" ? "wss:" : "ws:";
  const socketUrl = `${scheme}//${serverAddress.host}/ws`;
  recordDiagnostic("socket-connecting", { action, server: serverOrigin, room: roomCode || null,
    localOrigin: location.origin });
  let socket;
  try {
    const clientId = window.mapGameDiagnostics?.clientId;
    socket = new WebSocket(clientId ? `${socketUrl}?client=${encodeURIComponent(clientId)}` : socketUrl);
  } catch (error) {
    recordDiagnostic("socket-construction-failed", { message: error.message });
    state.multiplayer = false;
    showError(elements.multiplayerError, `Pripojenie sa nepodarilo: ${error.message}`);
    return;
  }
  state.socket = socket;
  elements.joinRoom.disabled = true;
  elements.createRoom.disabled = true;
  showError(elements.multiplayerError, "Pripájam sa k hostiteľovi…");
  let joined = false;
  const timeout = setTimeout(() => {
    if (state.socket !== socket || joined) return;
    recordDiagnostic("socket-timeout", { server: serverOrigin, readyState: socket.readyState });
    returnHome();
    showError(elements.multiplayerError, state.online
      ? "Server neodpovedá. Bezplatný server sa môže prebúdzať približne minútu. Počkaj a skús sa pripojiť znova."
      : "Hostiteľ neodpovedá. Skontroluj adresu, rovnakú sieť a Windows Firewall hostiteľa.");
  }, state.online ? 90000 : 10000);
  socket.addEventListener("open", () => {
    recordDiagnostic("socket-open", { server: serverOrigin });
    socket.send(JSON.stringify(action === "create"
      ? { type: "create", name }
      : { type: "join", name, roomCode }));
  }, { once: true });
  socket.addEventListener("message", event => {
    if (state.socket !== socket) return;
    try {
      const message = JSON.parse(event.data);
      if (message.type !== "tick" && message.type !== "guess-result") {
        recordDiagnostic("socket-message", { type: message.type, room: message.roomCode,
          region: message.regionId, status: message.status, playerCount: message.players?.length,
          error: message.type === "error" || message.type === "room-closed" ? message.message : undefined });
      }
      if (message.type === "error" && !joined) {
        clearTimeout(timeout);
        returnHome();
        showError(elements.multiplayerError, message.message);
        return;
      }
      if (message.type === "lobby-update" || message.type === "game-started") {
        const firstConfirmation = !joined;
        joined = true;
        clearTimeout(timeout);
        elements.joinRoom.disabled = false;
        elements.createRoom.disabled = false;
        showError(elements.multiplayerError, "");
        if (firstConfirmation && message.status === "lobby") setScreen("lobby");
      }
      handleMultiplayerMessage(message);
    } catch (error) {
      recordDiagnostic("socket-handler-failed", { message: error.message, stack: error.stack });
      console.error("Multiplayer message failed", error);
      if (!elements.gamePanel.hidden) showFeedback(error.message, "error");
      else showError(elements.lobbyError, error.message);
    }
  });
  socket.addEventListener("error", () => {
    recordDiagnostic("socket-error", { server: serverOrigin, readyState: socket.readyState });
    if (state.socket !== socket) return;
    const target = elements.lobbyPanel.hidden ? elements.multiplayerError : elements.lobbyError;
    showError(target, state.online
      ? "Spojenie so serverom sa nepodarilo. Skontroluj internet a skús to znova."
      : "Spojenie sa nepodarilo. Skontroluj, či je hra spustená v rovnakej sieti.");
  });
  socket.addEventListener("close", event => {
    recordDiagnostic("socket-close", { server: serverOrigin, code: event.code, reason: event.reason,
      wasClean: event.wasClean, joined });
    clearTimeout(timeout);
    if (state.socket !== socket) return;
    elements.joinRoom.disabled = false;
    elements.createRoom.disabled = false;
    clearInterval(state.syncTimer);
    if (!elements.endPanel.hidden) return;
    if (!joined) {
      returnHome();
      showError(elements.multiplayerError, state.online
        ? "Pripojenie zlyhalo. Server môže byť plný alebo sa prebúdzať; počkaj a skús to znova."
        : "Pripojenie zlyhalo. Skontroluj adresu hostiteľa, rovnakú sieť a Windows Firewall.");
      return;
    }
    showDisconnected("Spojenie s hostiteľom sa prerušilo.");
  });
}

function showDisconnected(message) {
  clearInterval(state.syncTimer);
  elements.hostStartOptions.hidden = true;
  if (!elements.gamePanel.hidden) {
    showFeedback(message, "error");
    elements.guessInput.disabled = true;
    elements.endGame.disabled = false;
    elements.endGame.textContent = "Späť na začiatok";
  } else {
    showError(elements.lobbyError, message);
    elements.leaveLobby.textContent = "Späť na začiatok";
  }
}

function handleMultiplayerMessage(message) {
  if (message.type === "room-closed") {
    showDisconnected(message.message);
    state.socket?.close();
    return;
  }
  if (message.type === "error") {
    if (!elements.gamePanel.hidden) showFeedback(message.message, "error");
    else showError(elements.lobbyError, message.message);
    return;
  }
  if (message.type === "lobby-update") {
    state.roomCode = message.roomCode;
    state.host = message.isHost;
    elements.roomCodeDisplay.textContent = message.roomCode;
    elements.hostStartOptions.hidden = !message.isHost || message.status !== "lobby";
    elements.lobbyStatus.textContent = message.status === "lobby" ? "Čaká sa na spustenie hry." : "Hra už prebieha.";
    renderPlayerList(message.players);
    if (message.isHost && message.status === "lobby") prepareInvite(message.roomCode);
    if (message.status === "lobby" && !state.syncTimer)
      state.syncTimer = setInterval(() => sendMultiplayer({ type: "sync" }), 30000);
    return;
  }
  if (message.type === "game-started") {
    clearInterval(state.syncTimer);
    state.gameMode = message.gameMode ?? "classic";
    state.battleRevision = -1;
    state.multiplayerFinished = false;
    renderGame(message.regionId, "time");
    updateMultiplayerProgress(message);
    recordDiagnostic("game-rendered", { region: message.regionId, shapes: elements.map.children.length });
    elements.timer.textContent = formatTime(message.remainingSeconds);
    setScreen("game");
    elements.inviteGameBanner.hidden = true;
    state.syncTimer = setInterval(() => sendMultiplayer({ type: "sync" }), 1000);
    return;
  }
  if (message.type === "guess-result") {
    if (state.multiplayerFinished) return;
    if (message.leaderboard || message.foundIds) updateMultiplayerProgress(message);
    showFeedback(message.message, message.outcome === "correct" ? "success" : "error");
    return;
  }
  if (message.type === "tick") {
    if (state.multiplayerFinished) return;
    elements.timer.textContent = formatTime(message.remainingSeconds);
    updateMultiplayerProgress(message);
    return;
  }
  if (message.type === "finished") {
    if (state.gameMode !== "classic") updateMultiplayerProgress(message);
    finishMultiplayer(message.results, message.reason);
  }
}

function renderPlayerList(players) {
  elements.playerList.replaceChildren();
  for (const player of players) {
    const item = document.createElement("li");
    item.textContent = player.name;
    const statusLabel = document.createElement("span");
    statusLabel.textContent = player.isHost ? "HOSTITEĽ" : "PRIPOJENÝ";
    item.append(statusLabel);
    elements.playerList.append(item);
  }
}

async function prepareInvite(roomCode) {
  if (state.inviteRoom === roomCode) return;
  state.inviteRoom = roomCode;
  try {
    if (state.online) {
      updateInvite();
      elements.inviteSection.hidden = false;
      return;
    }
    const response = await fetch("/api/network");
    if (!response.ok) throw new Error("Adresy hostiteľa sa nepodarilo načítať.");
    const addresses = await response.json();
    recordDiagnostic("invite-addresses", { addresses });
    if (!isLocalAddress(location.hostname) && !addresses.includes(location.origin)) addresses.unshift(location.origin);
    if (!addresses.length) throw new Error("Nenašla sa aktívna Wi-Fi/LAN adresa. Pripoj hostiteľa k miestnej sieti.");
    elements.networkAddress.replaceChildren();
    for (const address of addresses) {
      const option = document.createElement("option");
      option.value = address;
      option.textContent = address;
      elements.networkAddress.append(option);
    }
    if (addresses.includes(location.origin)) elements.networkAddress.value = location.origin;
    updateInvite();
    elements.inviteSection.hidden = false;
  } catch (error) {
    state.inviteRoom = "";
    showError(elements.lobbyError, error.message);
  }
}

function updateInvite() {
  const base = state.online ? location.origin : elements.networkAddress.value;
  elements.inviteLink.value = `${base}/?room=${state.roomCode}`;
  elements.serverAddress.value = base;
  recordDiagnostic("invite-selected", { address: base, room: state.roomCode });
}

function isLocalAddress(hostname) {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]";
}

function sendMultiplayer(message) {
  if (state.socket?.readyState === WebSocket.OPEN) {
    state.socket.send(JSON.stringify(message));
    return true;
  }
  const text = "Nie si pripojený k hostiteľovi. Vráť sa na začiatok a pripoj sa znova.";
  if (!elements.gamePanel.hidden) showFeedback(text, "error");
  else showError(elements.lobbyError, text);
  return false;
}

function startMultiplayerGame() {
  sendMultiplayer({
    type: "start",
    regionId: elements.hostRegion.value,
    gameMode: elements.hostGameMode.value === "battle" ? `battle-${elements.hostBattleVariant.value}` : "classic",
    timeLimitSeconds: Number(elements.hostMinutes.value) * 60
  });
}

function finishMultiplayer(results, reason) {
  clearInterval(state.syncTimer);
  state.syncTimer = null;
  state.multiplayerFinished = true;
  const ownName = state.playerName;
  const ownResult = results.find(result => result.name === ownName);
  const battle = state.gameMode !== "classic";
  const tiedWinner = ownResult?.place === 1 && results.filter(result => result.place === 1).length > 1;
  elements.endTitle.textContent = tiedWinner ? "Remíza!" : ownResult?.place === 1 ? "Vyhral si!" : "Hra sa skončila";
  elements.endSummary.textContent = reason === "host-ended"
    ? "Hostiteľ predčasne ukončil hru. Výsledky zobrazujú uhádnuté štáty do tohto okamihu."
    : battle ? "Map Battle: rozhoduje počet vlastnených štátov. Pri rovnosti bodov je remíza."
      : "Rozhoduje počet uhádnutých štátov; pri zhode čas, za ktorý ich hráč uhádol.";
  renderLeaderboard(elements.leaderboard, results);
  elements.leaderboardWrap.hidden = false;
  setScreen("end");
}

function renderLeaderboard(list, results) {
  list.replaceChildren();
  for (const result of results) {
    const item = document.createElement("li");
    const label = document.createElement("span");
    label.textContent = `${result.place}. ${result.name}`;
    if (result.color) {
      const swatch = document.createElement("span");
      swatch.className = "player-color";
      swatch.style.backgroundColor = result.color;
      swatch.setAttribute("aria-hidden", "true");
      label.prepend(swatch);
    }
    const score = document.createElement("strong");
    const elapsed = result.seconds === null ? "" : ` · ${formatTime(result.seconds)}`;
    score.textContent = `${result.guessedCount} / ${result.totalCount}${elapsed}`;
    item.append(label, score);
    list.append(item);
  }
}

function exitGame() {
  if (state.multiplayer) {
    if (state.socket?.readyState !== WebSocket.OPEN) {
      returnHome();
      return;
    }
    if (state.host) {
      if (window.confirm("Ukončiť multiplayer hru pre všetkých hráčov?")) {
        elements.endGame.disabled = sendMultiplayer({ type: "end" });
      }
      return;
    }

    if (window.confirm("Opustiť multiplayer hru? Tvoj výsledok sa nezapočíta.")) {
      sendMultiplayer({ type: "leave" });
      returnHome();
    }
    return;
  }

  void endSingleGame();
}

async function submitGuess(event) {
  event.preventDefault();
  const guess = elements.guessInput.value.trim();
  if (!guess) {
    elements.guessInput.focus();
    return;
  }

  elements.guessInput.value = "";
  try {
    if (state.multiplayer) {
      sendMultiplayer({ type: "guess", guess });
    } else if (state.singleGame) {
      await submitSingleGuess(guess);
    }
  } catch (error) {
    showFeedback(error.message || "Tip sa nepodarilo odoslať.", "error");
  }
  elements.guessInput.focus();
}

function returnHome() {
  clearInterval(state.syncTimer);
  clearInterval(state.singlePollTimer);
  state.syncTimer = null;
  state.singlePollTimer = null;
  state.multiplayer = false;
  state.singleGame = null;
  state.gameMode = "classic";
  state.battleRevision = -1;
  state.multiplayerFinished = false;
  const socket = state.socket;
  state.socket = null;
  if (socket && socket.readyState < WebSocket.CLOSING) socket.close();
  elements.joinRoom.disabled = false;
  elements.createRoom.disabled = false;
  elements.guessInput.disabled = false;
  elements.endGame.disabled = false;
  setScreen("home");
  showError(elements.singleError, "");
  showError(elements.multiplayerError, "");
  showError(elements.lobbyError, "");
}

elements.singleMode.addEventListener("change", () => {
  const timeMode = elements.singleMode.value === "time";
  elements.singleTimeOptions.hidden = !timeMode;
  elements.singleLivesOptions.hidden = timeMode;
});
function updateBattleOptions() {
  elements.battleOptions.hidden = elements.hostGameMode.value !== "battle";
}
elements.hostGameMode.addEventListener("change", updateBattleOptions);
window.addEventListener("pageshow", updateBattleOptions);
updateBattleOptions();
elements.startSingle.addEventListener("click", startSingleGame);
elements.createRoom.addEventListener("click", () => connectMultiplayer("create"));
elements.joinRoom.addEventListener("click", () => connectMultiplayer("join"));
elements.downloadDiagnostics.addEventListener("click", () => window.mapGameDiagnostics?.download());
elements.networkAddress.addEventListener("change", updateInvite);
elements.startMultiplayer.addEventListener("click", startMultiplayerGame);
elements.leaveLobby.addEventListener("click", returnHome);
elements.guessForm.addEventListener("submit", submitGuess);
elements.newGame.addEventListener("click", returnHome);
elements.copyInvite.addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(elements.inviteLink.value);
    elements.copyInvite.textContent = "Skopírované";
    setTimeout(() => { elements.copyInvite.textContent = "Kopírovať"; }, 1600);
  } catch {
    elements.inviteLink.select();
    document.execCommand("copy");
  }
});
elements.zoomIn.addEventListener("click", () => zoomMap(0.8));
elements.zoomOut.addEventListener("click", () => zoomMap(1.25));
elements.zoomReset.addEventListener("click", () => setMapView(mapBaseView));
elements.map.addEventListener("wheel", event => {
  event.preventDefault();
  zoomMap(event.deltaY < 0 ? 0.85 : 1.18, { x: event.clientX, y: event.clientY });
}, { passive: false });
elements.map.addEventListener("pointerdown", event => {
  if (event.button !== 0) return;
  const view = elements.map.getAttribute("viewBox").split(/\s+/).map(Number);
  mapDrag = {
    pointerId: event.pointerId,
    startX: event.clientX,
    startY: event.clientY,
    viewX: view[0],
    viewY: view[1],
    viewWidth: view[2],
    viewHeight: view[3]
  };
  elements.map.setPointerCapture(event.pointerId);
});
elements.map.addEventListener("pointermove", event => {
  if (!mapDrag || event.pointerId !== mapDrag.pointerId) return;
  const bounds = elements.map.getBoundingClientRect();
  setMapView({
    x: mapDrag.viewX - (event.clientX - mapDrag.startX) * mapDrag.viewWidth / bounds.width,
    y: mapDrag.viewY - (event.clientY - mapDrag.startY) * mapDrag.viewHeight / bounds.height,
    width: mapDrag.viewWidth
  });
});
elements.map.addEventListener("pointerup", () => { mapDrag = null; });
elements.map.addEventListener("pointercancel", () => { mapDrag = null; });
elements.endGame.addEventListener("click", exitGame);
elements.roomCodeInput.addEventListener("input", () => {
  elements.roomCodeInput.value = elements.roomCodeInput.value.toUpperCase().replace(/[^A-Z0-9]/g, "");
});
window.addEventListener("beforeunload", () => {
  if (state.socket && state.socket.readyState < WebSocket.CLOSING) state.socket.close();
});

initialize();
