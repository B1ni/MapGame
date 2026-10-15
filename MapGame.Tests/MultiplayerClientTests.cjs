const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function client() {
  const elements = new Map();
  const windowListeners = {};
  const element = id => {
    if (!elements.has(id)) elements.set(id, {
      value: "", textContent: "", hidden: id !== "homePanel", disabled: false,
      children: [], listeners: {}, classList: { toggle() {} },
      style: { removeProperty(name) { delete this[name]; } },
      addEventListener(type, handler) { this.listeners[type] = handler; },
      replaceChildren() { this.children = []; },
      append(...children) { this.children.push(...children); },
      prepend(child) { this.children.unshift(child); },
      focus() {}, setAttribute() {}, querySelectorAll() { return []; }
    });
    return elements.get(id);
  };
  class Socket {
    static OPEN = 1;
    static CLOSING = 2;
    static sockets = [];
    constructor(url) {
      this.url = url;
      this.readyState = 0;
      this.handlers = {};
      Socket.sockets.push(this);
    }
    addEventListener(type, handler) { this.handlers[type] = handler; }
    send(payload) { this.sent = JSON.parse(payload); }
    close() { this.readyState = 3; this.emit("close"); }
    emit(type, value = {}) { this.handlers[type]?.(value); }
    message(payload) { this.emit("message", { data: JSON.stringify(payload) }); }
  }
  const context = vm.createContext({
    console, URL, URLSearchParams, WebSocket: Socket,
    location: { origin: "http://localhost:5080", hostname: "localhost", search: "" },
    document: { getElementById: element, createElement: () => element(`new-${elements.size}`) },
    window: { addEventListener(type, handler) { windowListeners[type] = handler; } },
    fetch: async () => ({ ok: true, json: async () => ({ regions: [] }) }),
    setTimeout: () => 1, clearTimeout() {}, setInterval: () => 1, clearInterval() {}
  });
  const script = fs.readFileSync(path.join(__dirname, "..", "MapGame", "wwwroot", "app.js"), "utf8");
  vm.runInContext(script.replace(/\binitialize\(\);\s*$/, ""), context);
  return { element, Socket, windowListeners, run: script => vm.runInContext(script, context) };
}

test("restored Map Battle selection shows variants on pageshow without a change event", () => {
  const c = client();
  c.element("hostGameMode").value = "battle";
  c.windowListeners.pageshow();
  assert.equal(c.element("battleOptions").hidden, false);
  c.element("hostGameMode").value = "classic";
  c.windowListeners.pageshow();
  assert.equal(c.element("battleOptions").hidden, true);
});

test("connectMultiplayer requires a player's own name", () => {
  const c = client();
  c.run('connectMultiplayer("create")');
  assert.equal(c.Socket.sockets.length, 0);
  assert.match(c.element("multiplayerError").textContent, /meno/);
});

test("live leaderboard renders from start through ticks and guesses, then stays hidden in singleplayer", () => {
  const c = client();
  c.run('regions.set("europe", { name: "Europa" }); renderMap = () => {}; state.multiplayer = true;');
  const zero = [{ place: 1, name: "<Host>", guessedCount: 0, totalCount: 48, seconds: null }];
  c.run(`handleMultiplayerMessage(${JSON.stringify({
    type: "game-started", regionId: "europe", remainingSeconds: 60, leaderboard: zero
  })})`);
  assert.equal(c.element("liveLeaderboardWrap").hidden, false);
  assert.equal(c.element("liveLeaderboard").children[0].children[0].textContent, "1. <Host>");
  assert.equal(c.element("liveLeaderboard").children[0].children[1].textContent, "0 / 48");
  const leader = [{ place: 1, name: "Jana", guessedCount: 2, totalCount: 48, seconds: 5 }, ...zero];
  c.run(`handleMultiplayerMessage(${JSON.stringify({
    type: "tick", remainingSeconds: 55, foundIds: [], leaderboard: leader
  })})`);
  assert.equal(c.element("liveLeaderboard").children.length, 2);
  assert.equal(c.element("liveLeaderboard").children[0].children[1].textContent, "2 / 48 · 00:05");
  c.run(`handleMultiplayerMessage(${JSON.stringify({
    type: "guess-result", outcome: "correct", message: "OK", foundIds: ["SVK"], leaderboard: zero
  })})`);
  assert.equal(c.element("liveLeaderboard").children.length, 1);
  c.run(`handleMultiplayerMessage(${JSON.stringify({ type: "finished", results: leader })})`);
  assert.equal(c.element("endPanel").hidden, false);
  assert.equal(c.element("leaderboard").children.length, 2);
  c.run('state.multiplayer = false; renderGame("europe", "time");');
  assert.equal(c.element("liveLeaderboardWrap").hidden, true);
  assert.equal(c.element("liveLeaderboard").children.length, 0);
});

test("Map Battle selects variants and renders shared ownership, steals, tied results and stale updates safely", () => {
  const c = client();
  c.element("hostGameMode").value = "battle";
  c.element("hostBattleVariant").value = "hardcore";
  c.element("hostGameMode").listeners.change();
  assert.equal(c.element("battleOptions").hidden, false);
  c.run('state.socket = new WebSocket("ws://localhost/ws"); state.socket.readyState = WebSocket.OPEN; startMultiplayerGame();');
  assert.equal(c.Socket.sockets[0].sent.gameMode, "battle-hardcore");
  const shape = c.element("shape");
  shape.dataset = { id: "SVK" };
  c.element("map").querySelectorAll = () => [shape];
  c.run('regions.set("europe", {name:"Europa"}); renderMap = () => {}; state.multiplayer = true; state.playerName = "Host";');
  const results = [
    { place: 1, name: "Host", guessedCount: 1, totalCount: 48, seconds: null, color: "#2563eb" },
    { place: 1, name: "Guest", guessedCount: 1, totalCount: 48, seconds: null, color: "#dc2626" }
  ];
  const message = payload => c.run(`handleMultiplayerMessage(${JSON.stringify(payload)})`);
  message({ type: "game-started", gameMode: "battle-hardcore", regionId: "europe",
    remainingSeconds: 60, revision: 0, ownership: [], leaderboard: results });
  assert.match(c.element("gameModeLabel").textContent, /MAP BATTLE.*HARDCORE/);
  assert.equal(c.element("liveLeaderboard").children[0].children[0].children[0].style.backgroundColor, "#2563eb");
  message({ type: "tick", remainingSeconds: 58, revision: 1,
    ownership: [{ id: "SVK", color: "#2563eb" }], foundIds: [], leaderboard: results });
  assert.equal(shape.style.fill, "#2563eb");
  message({ type: "tick", remainingSeconds: 57, revision: 2,
    ownership: [{ id: "SVK", color: "#dc2626" }], foundIds: [], leaderboard: results });
  assert.equal(shape.style.fill, "#dc2626");
  message({ type: "tick", remainingSeconds: 58, revision: 1,
    ownership: [{ id: "SVK", color: "#2563eb" }], foundIds: [], leaderboard: [] });
  assert.equal(shape.style.fill, "#dc2626");
  assert.equal(c.element("liveLeaderboard").children.length, 2);
  message({ type: "finished", revision: 2, ownership: [{ id: "SVK", color: "#dc2626" }], results });
  assert.equal(c.element("endTitle").textContent, "Remíza!");
  assert.equal(c.element("endPanel").hidden, false);
  message({ type: "tick", remainingSeconds: 50, revision: 3, ownership: [], leaderboard: [] });
  assert.equal(shape.style.fill, "#dc2626");
  c.run("returnHome()");
  assert.equal(c.run("state.gameMode"), "classic");
  c.element("hostGameMode").value = "classic";
  c.element("hostGameMode").listeners.change();
  assert.equal(c.element("battleOptions").hidden, true);
});

test("connectMultiplayer normalizes IP and only opens a confirmed lobby", () => {
  const c = client();
  c.element("playerName").value = "Jana";
  c.element("hostAddressInput").value = "192.168.1.25";
  c.element("roomCodeInput").value = "ABC234";
  c.run('connectMultiplayer("join")');
  const socket = c.Socket.sockets[0];
  assert.equal(socket.url, "ws://192.168.1.25:5080/ws");
  assert.equal(c.element("lobbyPanel").hidden, true);
  socket.readyState = 1;
  socket.emit("open");
  assert.equal(socket.sent.name, "Jana");
  socket.message({ type: "lobby-update", roomCode: "ABC234", status: "lobby", isHost: false,
    players: [{ name: "Host", isHost: true }, { name: "Jana", isHost: false }] });
  assert.equal(c.element("lobbyPanel").hidden, false);
  assert.deepEqual(c.element("playerList").children.map(p => p.textContent), ["Host", "Jana"]);
});

test("connectMultiplayer unknown ID keeps player out of the lobby", () => {
  const c = client();
  c.element("playerName").value = "Jana";
  c.element("hostAddressInput").value = "192.168.1.25";
  c.element("roomCodeInput").value = "ABC234";
  c.run('connectMultiplayer("join")');
  c.Socket.sockets[0].message({ type: "error", message: "Hra s týmto ID neexistuje." });
  assert.equal(c.element("lobbyPanel").hidden, true);
  assert.equal(c.element("homePanel").hidden, false);
  assert.match(c.element("multiplayerError").textContent, /neexistuje/);
  assert.equal(c.element("joinRoom").disabled, false);
});

test("connectMultiplayer browser invite uses its host without a separate address", () => {
  const c = client();
  c.run('location.origin="http://192.168.1.25:5080"; location.hostname="192.168.1.25"; location.search="?room=ABC234"');
  c.element("playerName").value = "Peter";
  c.element("roomCodeInput").value = "ABC234";
  c.run('connectMultiplayer("join")');
  assert.equal(c.Socket.sockets[0].url, "ws://192.168.1.25:5080/ws");
});

test("connectMultiplayer reports an in-game disconnect on the visible screen", () => {
  const c = client();
  c.element("playerName").value = "Host";
  c.run('connectMultiplayer("create")');
  const socket = c.Socket.sockets[0];
  socket.message({ type: "lobby-update", roomCode: "ABC234", status: "lobby", isHost: false, players: [] });
  c.run('setScreen("game")');
  socket.close();
  assert.match(c.element("guessFeedback").textContent, /prerušilo/);
  assert.equal(c.element("guessInput").disabled, true);
  assert.equal(c.element("endGame").textContent, "Späť na začiatok");
  c.run("exitGame()");
  assert.equal(c.element("homePanel").hidden, false);
  assert.equal(c.element("guessInput").disabled, false);
});

test("connectMultiplayer late lobby updates do not return an active game to the lobby", () => {
  const c = client();
  c.element("playerName").value = "Host";
  c.run('connectMultiplayer("create")');
  const socket = c.Socket.sockets[0];
  const lobby = { type: "lobby-update", roomCode: "ABC234", status: "lobby", isHost: false, players: [] };
  socket.message(lobby);
  c.run('setScreen("game")');
  socket.message(lobby);
  assert.equal(c.element("gamePanel").hidden, false);
  assert.equal(c.element("lobbyPanel").hidden, true);
});

test("online mode hides LAN controls and joins over same-origin WSS ignoring LAN address", () => {
  const c = client();
  c.run('location.origin="https://map-game.onrender.com"; location.hostname="map-game.onrender.com"; configureHosting("internet")');
  for (const id of ["lanJoinOptions", "lanNetworkOptions", "lanServerOptions"])
    assert.equal(c.element(id).hidden, true);
  c.element("playerName").value = "Jana";
  c.element("roomCodeInput").value = "ABC234";
  c.element("hostAddressInput").value = "http://evil.example";
  c.run('connectMultiplayer("join")');
  assert.equal(c.Socket.sockets[0].url, "wss://map-game.onrender.com/ws");
});

test("online host invite uses public page origin without fetching LAN addresses", () => {
  const c = client();
  c.run('location.origin="https://map-game.onrender.com"; configureHosting("internet"); state.roomCode="ABC234"; prepareInvite("ABC234")');
  assert.equal(c.element("inviteLink").value, "https://map-game.onrender.com/?room=ABC234");
  assert.equal(c.element("inviteSection").hidden, false);
});

test("online join allows room ID alone and cold-start timeout is ninety seconds", () => {
  const c = client();
  c.run('configureHosting("internet"); globalThis.timeoutDelay=0; setTimeout=(_, delay) => { globalThis.timeoutDelay=delay; return 1; }');
  c.element("playerName").value = "Jana";
  c.element("roomCodeInput").value = "ABC234";
  c.run('connectMultiplayer("join")');
  assert.equal(c.Socket.sockets.length, 1);
  assert.equal(c.run("timeoutDelay"), 90000);
});
