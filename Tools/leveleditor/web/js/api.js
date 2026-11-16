/**
 * The browser side of the level editor API.
 *
 * One rule matters: the server is the authority.  This module never decides whether
 * an edit is valid and never writes placement.json - it posts one op and shows the
 * response.  The write token is kept in localStorage after the first `?token=` visit
 * and is only sent on writes.
 */

const TOKEN_KEY = "vrc-katamari-leveleditor.token";

export class ApiError extends Error {
  constructor(message, details = []) {
    super(message);
    this.name = "ApiError";
    this.details = details;
  }
}

export class API {
  constructor() {
    const params = new URLSearchParams(window.location.search);
    const fromUrl = params.get("token");
    if (fromUrl) {
      try {
        localStorage.setItem(TOKEN_KEY, fromUrl);
      } catch (error) {
        console.warn("could not persist the token", error);
      }
      params.delete("token");
      const query = params.toString();
      window.history.replaceState(
        null, "",
        window.location.pathname + (query ? `?${query}` : "") + window.location.hash
      );
    }
    this.token = (() => {
      try {
        return localStorage.getItem(TOKEN_KEY) || "";
      } catch (error) {
        return "";
      }
    })();
    this.version = 0;
  }

  get hasToken() {
    return Boolean(this.token);
  }

  forgetToken() {
    this.token = "";
    try {
      localStorage.removeItem(TOKEN_KEY);
    } catch (error) {
      /* localStorage may be disabled; writes then need the printed URL again */
    }
  }

  async _read(response) {
    const text = await response.text();
    let payload = null;
    try {
      payload = JSON.parse(text);
    } catch (error) {
      throw new ApiError(`服务器返回了非 JSON（HTTP ${response.status}）: ${text.slice(0, 200)}`);
    }
    if (!response.ok) {
      throw new ApiError(payload.error || `HTTP ${response.status}`, payload.details || []);
    }
    return payload;
  }

  async get(path) {
    if (!this.token) {
      throw new ApiError(
        "还没有写入令牌：请用服务器启动日志里打印的 ?token=… 链接打开本页（或重新打开那个链接）"
      );
    }
    const response = await fetch(path, {
      headers: { Accept: "application/json", "X-Editor-Token": this.token },
    });
    const data = await this._read(response);
    if (data && data.ok === false && /token/i.test(data.error || "")) {
      this.forgetToken();
      throw new ApiError(
        "令牌无效或已更换。请用服务器日志里最新的 ?token=… 链接重新打开本页。",
        data.details || []
      );
    }
    return data;
  }

  async post(payload) {
    if (!this.token) {
      throw new ApiError(
        "还没有写入令牌：请用服务器启动日志里打印的 ?token=… 链接打开本页（或重新打开那个链接）"
      );
    }
    const response = await fetch("/api/op", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Editor-Token": this.token,
      },
      body: JSON.stringify(payload),
    });
    const data = await this._read(response);
    if (data && data.ok === false && /token/i.test(data.error || "")) {
      this.forgetToken();
      throw new ApiError(
        "写入令牌无效或已更换。请用服务器日志里最新的 ?token=… 链接重新打开本页。",
        data.details || []
      );
    }
    return data;
  }

  op(name, args = {}) {
    return this.post({ op: name, args });
  }

  openLevel(level, options = {}) {
    if (!this.token) {
      throw new ApiError("打开关卡需要写入令牌（?token=… 链接）");
    }
    return fetch("/api/open", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Editor-Token": this.token,
      },
      body: JSON.stringify({ level, ...options }),
    }).then((response) => this._read(response));
  }

  loadLevel() {
    return this.get("/api/level");
  }

  checkReport() {
    return this.get("/api/check");
  }

  hints(diameter) {
    return this.get(`/api/hints?d=${encodeURIComponent(diameter.toFixed(3))}`);
  }

  loadRegions() {
    return this.get("/api/regions");
  }

  async saveRegions(regions) {
    if (!this.token) {
      throw new ApiError("还没有写入令牌");
    }
    const response = await fetch("/api/regions", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Editor-Token": this.token,
      },
      body: JSON.stringify({ regions }),
    });
    return this._read(response);
  }

  stats(params) {
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(params)) {
      if (value === undefined || value === null || value === "") continue;
      query.set(key, Array.isArray(value) ? value.join(",") : String(value));
    }
    return this.get(`/api/stats?${query.toString()}`);
  }

  /**
   * Follow saves from other tabs/agents.  EventSource reconnects by itself; the
   * caller refetches `/api/level` whenever the version moves.
   */
  connectEvents(onVersion, onStatus = () => {}) {
    if (!("EventSource" in window)) {
      return null;
    }
    const source = new EventSource(`/api/events?token=${encodeURIComponent(this.token)}`);
    source.addEventListener("change", (event) => {
      try {
        const data = JSON.parse(event.data);
        if (data.version >= this.version) {
          this.version = data.version;
          onVersion(data.version);
        }
      } catch (error) {
        console.warn("bad event", error);
      }
    });
    source.addEventListener("open", () => onStatus("live"));
    source.addEventListener("error", () => onStatus("retrying"));
    return source;
  }
}
