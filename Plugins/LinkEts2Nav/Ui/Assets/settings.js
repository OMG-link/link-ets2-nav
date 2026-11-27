"use strict";
(() => {
  const $ = id => document.getElementById(id);
  const token = location.hash.slice(1) || history.state?.settingsAccess;
  if (location.hash) history.replaceState({settingsAccess: token}, "", location.pathname);
  const routeNames = {Available:"已设定路线", None:"未设定路线", Unknown:"等待游戏数据", Invalid:"路线更新中", Stale:"等待路线刷新"};
  let phase = "Idle", timer, revision = 0, starting = false, cancelling = false, generationError = "", configuredDirectory = "";

  function feedback(id, message, error = false) {
    const el = $(id);
    el.textContent = message || "";
    el.hidden = !message;
    el.classList.toggle("error", error);
  }
  async function api(path, body) {
    if (!token) throw new Error("请从导航宿主重新打开设置页面。");
    const options = {headers:{Authorization:"Bearer " + token}, cache:"no-store", credentials:"omit", redirect:"error"};
    if (body !== undefined) {
      options.method = "POST";
      options.headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(body);
    }
    let response;
    try { response = await fetch("/api/" + path, options); }
    catch { throw new Error("无法连接导航宿主，请检查宿主是否仍在运行。"); }
    const result = response.headers.get("content-type")?.includes("json") ? await response.json() : {};
    if (!response.ok) throw new Error(result.message || (response.status === 401 || response.status === 403
      ? "访问已过期，请从导航宿主重新打开设置页面。" : "操作未完成，请再试一次。"));
    return result;
  }
  function jobView(job) {
    phase = job.phase;
    const running = phase === "Running";
    $("generation-fields").disabled = running || starting;
    $("cancel").hidden = !running;
    $("cancel").disabled = cancelling;
    $("job").hidden = phase === "Idle" && !generationError;
    $("progress").hidden = !running;
    if (running && job.total > 0) {
      $("progress").max = job.total;
      $("progress").value = job.completed;
    } else $("progress").removeAttribute("value");
    const count = running && job.total > 0 ? "（" + job.completed + " / " + job.total + "）" : "";
    feedback("job-message", generationError || (job.message + count), Boolean(generationError) || phase === "Failed");
  }
  async function refresh(pack = false) {
    const request = ++revision;
    clearTimeout(timer);
    try {
      const state = await api("status" + (pack ? "?pack=1" : ""));
      if (request !== revision) return;
      feedback("connection", "");
      $("navigation").textContent = state.navigation;
      feedback("next", state.next ? "下一步：" + (state.nextMeters > 0 ? state.nextMeters + " 米后，" : "") + state.next : "");
      $("route").textContent = routeNames[state.route] || "等待路线数据";
      $("replay-section").hidden = !state.replay;
      if (state.pack) {
        configuredDirectory = state.pack.configuredDirectory;
        if (!$("directory").value) $("directory").value = state.pack.configuredDirectory;
      }
      if (state.activeDirectory) $("pack-info").textContent = "当前生效：" + state.activeDirectory;
      else if (state.packError) $("pack-info").textContent = state.packError;
      else if (configuredDirectory) $("pack-info").textContent = "已配置：" + configuredDirectory;
      const done = state.job.phase === "Succeeded" && phase !== "Succeeded";
      jobView(state.job);
      if (done && !pack) { void refresh(true); return; }
    } catch (error) {
      if (request !== revision) return;
      feedback("connection", error.message, true);
    } finally {
      if (request === revision && !document.hidden && token)
        timer = setTimeout(refresh, phase === "Running" ? 900 : 4000);
    }
  }
  $("generation-form").addEventListener("submit", async event => {
    event.preventDefault();
    if (starting || phase === "Running") return;
    generationError = "";
    const template = $("request-template").value.trim();
    if (template) {
      try { JSON.parse(template); }
      catch { $("job").hidden = false; feedback("job-message", "请求模板不是有效的 JSON。", true); return; }
    }
    const input = {endpoint:$("endpoint").value.trim(), model:$("model").value.trim(), voice:$("voice").value.trim(),
      apiKey:$("api-key").value || null, top:$("top").valueAsNumber, requestTemplate:template || null,
      authHeader:$("auth-header").value.trim() || "Authorization", authPrefix:$("auth-prefix").value};
    $("api-key").value = "";
    starting = true;
    $("generation-fields").disabled = true;
    try { await api("speech/generate", input); }
    catch (error) { generationError = error.message; }
    finally {
      starting = false;
      $("generation-fields").disabled = phase === "Running";
      if (generationError) { $("job").hidden = false; feedback("job-message", generationError, true); }
      void refresh(true);
    }
  });
  $("cancel").addEventListener("click", async () => {
    cancelling = true;
    $("cancel").disabled = true;
    try { await api("speech/cancel", {}); }
    catch (error) { generationError = error.message; }
    finally { cancelling = false; void refresh(); }
  });
  function apply(form, button, output, path, body, after) {
    $(form).addEventListener("submit", async event => {
      event.preventDefault();
      if ($(button).disabled) return;
      $(button).disabled = true;
      feedback(output, "");
      try { const result = await api(path, body()); feedback(output, result.message); after(); }
      catch (error) { feedback(output, error.message, true); }
      finally { $(button).disabled = false; }
    });
  }
  apply("import-form", "import-pack", "pack-message", "speech/import", () => ({directory:$("directory").value.trim()}), () => void refresh(true));
  apply("replay-form", "save-replay", "replay-message", "replay", () => ({comment:$("comment").value}), () => { $("comment").value = ""; });
  document.addEventListener("visibilitychange", () => { if (document.hidden) clearTimeout(timer); else void refresh(true); });
  void refresh(true);
})();
