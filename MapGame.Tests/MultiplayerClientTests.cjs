const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function client() {
  const elements = new Map();
  const element = id => {
    if (!elements.has(id)) elements.set(id, {
      value: "", textContent: "", hidden: id !== "homePanel", disabled: false,
      children: [], listeners: {}, classList: { toggle() {} },
      addEventListener(type, handler) { this.listeners[type] = handler; },
      replaceChildren() { this.children = []; },
      append(child) { this.children.push(child); },
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
    window: { addEventListener() {} },
    fetch: async () => ({ ok: true, json: async () => ({ regions: [] }) }),
    setTimeout: () => 1, clearTimeout() {}, setInterval: () => 1, clearInterval() {}
  });
  const script = fs.readFileSync(path.join(__dirname, "..", "MapGame", "wwwroot", "app.js"), "utf8");
  vm.runInContext(script.replace(/\binitialize\(\);\s*$/, ""), context);
  return { element, Socket, run: script => vm.runInContext(script, context) };
}

test("connectMultiplayer requires a player's own name", () => {
  const c = client();
  c.run('connectMultiplayer("create")');
  assert.equal(c.Socket.sockets.length, 0);
  assert.match(c.element("multiplayerError").textContent, /meno/);
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
