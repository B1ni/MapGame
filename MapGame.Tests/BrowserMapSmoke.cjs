// Run against a local server and a separate Chromium debugging profile; no npm packages required.
const assert = require("node:assert/strict");
const { setTimeout: delay } = require("node:timers/promises");
const base = process.env.MAPGAME_URL || "http://localhost:5080";
const debuggerUrl = process.env.MAPGAME_CDP || "http://127.0.0.1:9229";

(async () => {
  const version = await (await fetch(`${debuggerUrl}/json/version`)).json();
  const ws = new WebSocket(version.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    ws.addEventListener("open", resolve, { once: true });
    ws.addEventListener("error", reject, { once: true });
  });
  let sequence = 0;
  const pending = new Map();
  const exceptions = [];
  const targets = [];
  ws.addEventListener("message", event => {
    const message = JSON.parse(event.data);
    if (message.id) {
      const entry = pending.get(message.id);
      pending.delete(message.id);
      if (message.error) entry.reject(new Error(JSON.stringify(message.error)));
      else entry.resolve(message.result);
    } else if (message.method === "Runtime.exceptionThrown") {
      exceptions.push(message.params.exceptionDetails);
    }
  });
  const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`CDP timed out: ${method}`));
    }, 15000);
    pending.set(id, {
      resolve: value => { clearTimeout(timer); resolve(value); },
      reject: error => { clearTimeout(timer); reject(error); }
    });
    ws.send(JSON.stringify({ id, method, params, sessionId }));
  });
  const evaluate = async (session, expression) => {
    const result = await send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true }, session);
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  };
  const waitFor = async (session, expression) => {
    for (let i = 0; i < 100; i++) {
      const value = await evaluate(session, expression);
      if (value) return value;
      await delay(100);
    }
    throw new Error(`Timed out: ${expression}`);
  };
  const open = async (url = base) => {
    const { targetId } = await send("Target.createTarget", { url });
    targets.push(targetId);
    const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
    await send("Runtime.enable", {}, sessionId);
    await waitFor(sessionId, 'document.querySelector("#hostRegion")?.options.length === 7');
    return sessionId;
  };
  const guess = async (session, value) => {
    await evaluate(session, `elements.guessInput.value=${JSON.stringify(value)}; elements.guessForm.dispatchEvent(new Event("submit",{cancelable:true}))`);
    await waitFor(session, "!elements.guessInput.disabled");
  };
  const solved = session => evaluate(session, 'Array.from(elements.map.querySelectorAll(".solved")).map(p=>p.dataset.id).sort()');
  const verifyMap = async session => {
    assert.equal(await evaluate(session, 'elements.map.querySelectorAll("[data-id]").length'), 49);
    assert.equal(await evaluate(session, '!!elements.map.querySelector("[data-id=GBR]")'), false);
    const paths = await evaluate(session, `["GB-ENG","GB-SCT","GB-WLS","GB-NIR"].map(id=>{
      const p=elements.map.querySelector('[data-id="'+id+'"]');
      return {id,d:p?.getAttribute("d"),width:p?.getBBox().width,height:p?.getBBox().height};
    })`);
    assert.equal(new Set(paths.map(p => p.d)).size, 4);
    for (const path of paths) {
      assert.ok(path.d.length > 500, `${path.id}: missing detailed outline`);
      assert.ok(path.width > 0 && path.height > 0, `${path.id}: invisible geometry`);
    }
  };
  try {
    const shapes = await (await fetch(`${base}/data/map-shapes.json`)).json();
    for (const region of shapes.regions)
      for (const feature of region.features)
        assert.deepEqual(Object.keys(feature).sort(), ["geometry", "id"]);
    assert.equal((await fetch(`${base}/data/country-names.json`)).status, 404);
    assert.equal((await fetch(`${base}/favicon.svg`)).status, 200);

    const single = await open();
    await evaluate(single, 'elements.singleRegion.value="europe"; elements.startSingle.click()');
    await waitFor(single, '!elements.gamePanel.hidden && elements.map.querySelector("[data-id=GB-ENG]")');
    await verifyMap(single);
    await guess(single, "Anglicko");
    await waitFor(single, 'elements.map.querySelector("[data-id=GB-ENG]").classList.contains("solved")');
    assert.deepEqual(await solved(single), ["GB-ENG"]);
    await guess(single, "ENG");
    await waitFor(single, 'elements.guessFeedback.textContent.includes("už máš")');
    assert.deepEqual(await solved(single), ["GB-ENG"]);
    await guess(single, "UK");
    await waitFor(single, 'elements.guessFeedback.classList.contains("incorrect") || elements.guessFeedback.textContent.includes("nie je")');
    assert.deepEqual(await solved(single), ["GB-ENG"]);
    for (const [answer, id] of [["Skotsko", "GB-SCT"], ["WAL", "GB-WLS"], ["Northern Ireland", "GB-NIR"]]) {
      await guess(single, answer);
      await waitFor(single, `elements.map.querySelector('[data-id="${id}"]').classList.contains("solved")`);
    }
    assert.deepEqual(await solved(single), ["GB-ENG", "GB-NIR", "GB-SCT", "GB-WLS"]);
    await guess(single, "Kosovo");
    await waitFor(single, 'elements.map.querySelector("[data-id=KOS]").classList.contains("solved")');
    assert.deepEqual(await solved(single), ["GB-ENG", "GB-NIR", "GB-SCT", "GB-WLS", "KOS"]);
    assert.match(await evaluate(single, "elements.guessHistory.textContent"), /Kosovo/);
    assert.equal(await evaluate(single, 'document.querySelector(\'link[rel="icon"]\').getAttribute("href")'), "/favicon.svg");
    await evaluate(single, 'fetch("/api/games/"+state.singleGame.id+"/finish",{method:"POST"}).then(r=>r.json())');
    await evaluate(single, 'returnHome(); elements.singleRegion.value="north-america"; elements.startSingle.click()');
    await waitFor(single, '!elements.gamePanel.hidden && elements.map.querySelector("[data-id=USA]")');
    assert.equal(await evaluate(single, 'elements.map.querySelectorAll("[data-id]").length'), 23);
    await guess(single, "Spojene staty americke");
    await waitFor(single, 'elements.map.querySelector("[data-id=USA]").classList.contains("solved")');
    assert.deepEqual(await solved(single), ["USA"]);
    await evaluate(single, 'fetch("/api/games/"+state.singleGame.id+"/finish",{method:"POST"}).then(r=>r.json())');

    const host = await open();
    await evaluate(host, 'elements.playerName.value="Host"; elements.hostRegion.value="europe"; elements.createRoom.click()');
    const code = await waitFor(host, '!elements.lobbyPanel.hidden && elements.roomCodeDisplay.textContent');
    const guest = await open(`${base}/?room=${code}`);
    await evaluate(guest, 'elements.playerName.value="Guest"; elements.joinRoom.click()');
    await waitFor(host, "elements.playerList.children.length === 2");
    await waitFor(guest, "!elements.lobbyPanel.hidden");
    await evaluate(host, "elements.startMultiplayer.click()");
    for (const session of [host, guest]) {
      await waitFor(session, '!elements.gamePanel.hidden && elements.map.querySelector("[data-id=GB-NIR]")');
      await verifyMap(session);
    }
    for (const [answer, id] of [["ENG", "GB-ENG"], ["Scotland", "GB-SCT"], ["Wales", "GB-WLS"], ["Severne Irsko", "GB-NIR"]]) {
      await guess(guest, answer);
      await waitFor(guest, `elements.map.querySelector('[data-id="${id}"]').classList.contains("solved")`);
      assert.deepEqual(await solved(host), []);
    }
    assert.equal(await evaluate(host, "elements.guessHistory.children.length"), 0);
    assert.equal(await evaluate(guest, "elements.guessHistory.children.length"), 4);
    await guess(guest, "GB-ENG");
    await waitFor(guest, 'elements.guessFeedback.textContent.includes("už máš")');
    await guess(guest, "Británia");
    await waitFor(guest, 'elements.guessFeedback.textContent.includes("nie je")');
    assert.deepEqual(await solved(guest), ["GB-ENG", "GB-NIR", "GB-SCT", "GB-WLS"]);
    await evaluate(host, 'sendMultiplayer({type:"end"})');
    for (const session of [host, guest])
      await waitFor(session, "!elements.endPanel.hidden && elements.leaderboard.children.length === 2");
    assert.match(await evaluate(guest, "elements.leaderboard.textContent"), /Guest/);

    for (const variant of ["normal", "hardcore"]) {
      const battleHost = host;
      const battleGuest = guest;
      await evaluate(battleHost, "elements.returnLobby.click()");
      for (const session of [battleHost, battleGuest]) {
        await waitFor(session, "!elements.lobbyPanel.hidden");
        assert.equal(await evaluate(session, "state.socket.readyState"), 1);
        assert.equal(await evaluate(session, "state.roomCode"), code);
        assert.equal(await evaluate(session, "elements.playerList.children.length"), 2);
      }
      await evaluate(battleHost, `elements.hostRegion.value="south-america";
        elements.hostGameMode.value="battle"; elements.hostGameMode.dispatchEvent(new Event("change"));
        elements.hostBattleVariant.value=${JSON.stringify(variant)}; elements.hostBattleVariant.dispatchEvent(new Event("change"))`);
      await waitFor(battleGuest, `elements.lobbySettingsSummary.textContent.includes("${variant === "normal" ? "Normal" : "Hardcore"}")`);
      assert.equal(await evaluate(battleGuest, "elements.hostStartOptions.hidden"), true);
      await evaluate(battleHost, "elements.startMultiplayer.click()");
      for (const session of [battleHost, battleGuest])
        await waitFor(session, '!elements.gamePanel.hidden && elements.gameModeLabel.textContent.includes("MAP BATTLE")');
      const colors = await evaluate(battleHost, `Object.fromEntries(Array.from(elements.liveLeaderboard.children).map(li=>
        [li.querySelector("span").textContent.replace(/^\\d+\\. /,""),li.querySelector(".player-color").style.backgroundColor]))`);
      assert.notEqual(colors.Host, colors.Guest);
      const argColor = 'elements.map.querySelector("[data-id=ARG]").style.fill';
      await guess(battleHost, "ARG");
      for (const session of [battleHost, battleGuest])
        await waitFor(session, `${argColor} === ${JSON.stringify(colors.Host)}`);
      for (const session of [battleHost, battleGuest]) {
        await waitFor(session, 'elements.battleTotal.textContent === "Obsadené štáty: 1 / 12"');
        assert.equal(await evaluate(session, "elements.guessHistoryWrap.hidden"), variant === "hardcore");
        if (variant === "hardcore") assert.equal(await evaluate(session, "elements.guessHistory.children.length"), 0);
        else await waitFor(session, 'elements.guessHistory.textContent.includes("Argentína")');
      }
      await guess(battleGuest, "ARG");
      if (variant === "normal") {
        await waitFor(battleGuest, 'elements.guessFeedback.textContent.includes("obsadil")');
        for (const session of [battleHost, battleGuest])
          assert.equal(await evaluate(session, argColor), colors.Host);
        await guess(battleGuest, "BRA");
        await waitFor(battleHost, 'elements.map.querySelector("[data-id=BRA]").style.fill !== ""');
        await evaluate(battleHost, 'sendMultiplayer({type:"end"})');
        for (const session of [battleHost, battleGuest]) {
          await waitFor(session, '!elements.endPanel.hidden && elements.endTitle.textContent === "Remíza!"');
          const scores = await evaluate(session, 'Array.from(elements.leaderboard.querySelectorAll("strong")).map(s=>s.textContent)');
          assert.deepEqual(scores, ["1 / 12", "1 / 12"]);
        }
      } else {
        for (const session of [battleHost, battleGuest])
          await waitFor(session, `${argColor} === ${JSON.stringify(colors.Guest)}`);
        assert.deepEqual(await evaluate(battleHost,
          'Array.from(elements.liveLeaderboard.querySelectorAll("strong")).map(s=>s.textContent)'), ["1 / 12", "0 / 12"]);
        const ids = shapes.regions.find(region => region.id === "south-america").features.map(feature => feature.id);
        for (const id of ids.filter(id => id !== "ARG")) {
          await guess(battleHost, id);
          await waitFor(battleHost, id === ids.filter(id => id !== "ARG").at(-1)
            ? "!elements.endPanel.hidden"
            : `elements.map.querySelector('[data-id="${id}"]').style.fill !== ""`);
        }
        for (const session of [battleHost, battleGuest]) {
          await waitFor(session, "!elements.endPanel.hidden");
          assert.deepEqual(await evaluate(session,
            'Array.from(elements.leaderboard.querySelectorAll("strong")).map(s=>s.textContent)'), ["11 / 12", "1 / 12"]);
        }
      }
    }
    assert.deepEqual(exceptions, []);
    console.log("PASS: Chromium singleplayer, USA country, classic and reusable Battle lobby; private/shared/no Hardcore histories, total occupancy, settings visible to guests, shared colors, stealing, draws, full-map finish, favicon.");
  } finally {
    for (const targetId of targets) await send("Target.closeTarget", { targetId });
    ws.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
