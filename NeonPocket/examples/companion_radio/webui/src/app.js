import {createCommandDiscovery} from './command-discovery.js';
import {createSituationMap} from "./situation-map.js";
import {createSecureComs} from './secure-coms.js';
import {createMonitoring} from './monitoring.js';
import { createContactMap } from "./contact-map.js";
import { createFavorites } from "./favorites.js";
import { createContactsTools, matchesConversation } from "./contacts.js";
import { createKeywordWatch } from "./keyword-watch.js";
import { createRepeaterTools } from "./repeaters.js";
import Connection from "@liamcottle/meshcore.js/src/connection/connection.js";
import Constants from "@liamcottle/meshcore.js/src/constants.js";

const MAX_FRAME_BYTES = 176;
const MAX_MESSAGE_BYTES = 140;
const MAX_HISTORY_MESSAGES = 5000;
const POLL_DELAY_MS = 90;
const COMMAND_TIMEOUT_MS = 30000;
const MAX_RECOVERY_ATTEMPTS = 2;
const RADIO_LEASE_KEY = "rcc6-radio-owner";
const RADIO_LEASE_MS = 7000;
const RADIO_HEARTBEAT_MS = 2000;
const $ = (id) => document.getElementById(id);
const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const encoder = new TextEncoder();

function createSessionId() {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return [...bytes].map((value) => value.toString(16).padStart(2, "0")).join("");
}

const TAB_OWNER_ID = createSessionId();

function loadSessionId() {
  try {
    const saved = localStorage.getItem("rcc6-session");
    if (/^[0-9a-f]{32}$/.test(saved || "")) return saved;
    const created = createSessionId();
    localStorage.setItem("rcc6-session", created);
    return created;
  } catch {
    return createSessionId();
  }
}

class HttpPollingConnection extends Connection {
  constructor() {
    super();
    this.running = false;
    this.failures = 0;
    this.interrupted = false;
    this.sessionId = loadSessionId();
    this.lastDispatchedSequence = 0;
    this.pendingAck = 0;
    this.ready = false;
    this.connecting = false;
    this.drainTask = null;
    this.leaseTimer = null;
    this.storageListening = false;
    this.onStorage = (event) => {
      if (event.key === RADIO_LEASE_KEY && this.running && !this.ownsLease()) this.handleLeaseLoss();
    };
  }

  onContactMsgRecvResponse(reader, isV3) {
    let snr = null;
    if (isV3) { snr = reader.readInt8() / 4; reader.readByte(); reader.readByte(); }
    const pubKeyPrefix = reader.readBytes(6), pathLen = reader.readByte(), txtType = reader.readByte(), senderTimestamp = reader.readUInt32LE();
    if (txtType === Constants.TxtTypes.SignedPlain && reader.getRemainingBytesCount() < 4) return;
    const authorPrefix = txtType === Constants.TxtTypes.SignedPlain ? reader.readBytes(4) : undefined;
    this.emit(Constants.ResponseCodes.ContactMsgRecv, { pubKeyPrefix, pathLen, txtType, senderTimestamp, authorPrefix, text: reader.readString(), snr });
  }

  async connect() {
    if (this.running || this.connecting) return;
    this.connecting = true;
    try {
      if (!this.storageListening) {
        window.addEventListener("storage", this.onStorage);
        this.storageListening = true;
      }
      if (!await this.claimLease()) {
        this.emit("transport-busy");
        return;
      }
      this.running = true;
      this.ready = false;
      this.abortController = new AbortController();
      this.drainTask = this.drainRetainedFrames();
      const drained = await this.drainTask;
      if (!drained || !this.running || !this.ownsLease()) return;
      this.ready = true;
      this.pollTask = this.poll();
      this.emit("connected");
    } finally {
      this.connecting = false;
    }
  }

  async close() {
    const wasReady = this.ready;
    this.running = false;
    this.ready = false;
    this.abortController?.abort();
    try { await this.drainTask; } catch { /* aborted */ }
    try { await this.pollTask; } catch { /* aborted */ }
    if (wasReady) this.onDisconnected();
    this.releaseLease();
    if (this.storageListening) window.removeEventListener("storage", this.onStorage);
    this.storageListening = false;
  }

  readLease() {
    try {
      const lease = JSON.parse(localStorage.getItem(RADIO_LEASE_KEY) || "null");
      return lease && typeof lease.owner === "string" && Number.isFinite(lease.expires) ? lease : null;
    } catch { return null; }
  }

  ownsLease() {
    const lease = this.readLease();
    return lease?.owner === TAB_OWNER_ID && lease.expires > Date.now();
  }

  writeLease() {
    try {
      localStorage.setItem(RADIO_LEASE_KEY, JSON.stringify({ owner: TAB_OWNER_ID, expires: Date.now() + RADIO_LEASE_MS }));
      return this.ownsLease();
    } catch { return false; }
  }

  async claimLease() {
    const current = this.readLease();
    if (current && current.owner !== TAB_OWNER_ID && current.expires > Date.now()) return false;
    if (!this.writeLease()) return false;
    await delay(80);
    if (!this.ownsLease()) return false;
    this.stopHeartbeat();
    this.leaseTimer = setInterval(() => {
      if (!this.ownsLease() || !this.writeLease()) this.handleLeaseLoss();
    }, RADIO_HEARTBEAT_MS);
    return true;
  }

  stopHeartbeat() {
    clearInterval(this.leaseTimer);
    this.leaseTimer = null;
  }

  handleLeaseLoss() {
    this.stopHeartbeat();
    if (!this.running) return;
    this.running = false;
    this.ready = false;
    this.abortController?.abort();
    this.emit("transport-busy");
  }

  releaseLease() {
    this.stopHeartbeat();
    try {
      if (this.readLease()?.owner === TAB_OWNER_ID) localStorage.removeItem(RADIO_LEASE_KEY);
    } catch { /* storage unavailable */ }
  }

  leavePage() {
    this.running = false;
    this.ready = false;
    this.abortController?.abort();
    this.releaseLease();
  }

  async sendToRadioFrame(data) {
    const frame = data instanceof Uint8Array ? data : new Uint8Array(data);
    if (!frame.length || frame.length > MAX_FRAME_BYTES) throw new Error("Invalid companion frame size");
    if (!this.ready) throw new Error("Companion link is still draining");
    if (!this.ownsLease()) {
      this.handleLeaseLoss();
      throw new Error("Another tab owns the companion link");
    }
    try {
      const response = await fetch("/api/frame", {
        method: "POST",
        headers: {
          "Content-Type": "application/octet-stream",
          "X-RCC6-Session": this.sessionId,
        },
        body: frame,
        cache: "no-store",
        credentials: "same-origin",
        signal: this.abortController.signal,
      });
      if (!response.ok) throw new Error(`Frame POST failed (${response.status})`);
      this.markHealthy();
    } catch (error) {
      if (error.name !== "AbortError") {
        error.rcc6TransportFailure = true;
        this.markFailure(error);
      }
      throw error;
    }
  }

  async fetchFrame() {
    const headers = { Accept: "application/octet-stream", "X-RCC6-Session": this.sessionId };
    if (this.pendingAck) headers["X-RCC6-Ack"] = String(this.pendingAck);
    const response = await fetch("/api/frame", { headers, cache: "no-store", credentials: "same-origin", signal: this.abortController.signal });
    if (response.status === 204) {
      this.pendingAck = 0;
      this.markHealthy();
      return false;
    }
    if (!response.ok) throw new Error(`Frame poll failed (${response.status})`);
    const sequence = Number(response.headers.get("X-RCC6-Seq"));
    if (!Number.isInteger(sequence) || sequence < 1 || sequence > 0xffffffff) throw new Error("Invalid frame sequence");
    const frame = new Uint8Array(await response.arrayBuffer());
    if (!frame.length || frame.length > MAX_FRAME_BYTES) throw new Error("Invalid radio frame");
    if (sequence !== this.lastDispatchedSequence) {
      this.onFrameReceived(frame);
      this.lastDispatchedSequence = sequence;
      // MeshCore.js defers `once` handlers twice; let message persistence finish before ACK.
      await delay(0);
      await delay(0);
    }
    this.pendingAck = sequence;
    this.markHealthy();
    return true;
  }

  async drainRetainedFrames() {
    while (this.running) {
      if (!this.ownsLease()) { this.handleLeaseLoss(); return false; }
      try {
        if (!await this.fetchFrame()) return true;
      } catch (error) {
        if (!this.running || error.name === "AbortError") return false;
        this.markFailure(error);
        await delay(Math.min(250 * 2 ** Math.min(this.failures, 3), 2000));
      }
    }
    return false;
  }

  async poll() {
    while (this.running) {
      if (!this.ownsLease()) { this.handleLeaseLoss(); break; }
      try {
        if (!await this.fetchFrame()) await delay(POLL_DELAY_MS);
      } catch (error) {
        if (!this.running || error.name === "AbortError") break;
        this.markFailure(error);
        await delay(Math.min(250 * 2 ** Math.min(this.failures, 3), 2000));
      }
    }
  }

  markFailure(error) {
    this.failures += 1;
    if (this.failures >= 3 && !this.interrupted) {
      this.interrupted = true;
      this.emit("transport-state", "reconnecting", error);
    }
  }

  markHealthy() {
    const restored = this.interrupted;
    this.failures = 0;
    this.interrupted = false;
    if (restored && this.ready) this.emit("transport-restored");
  }
}

const state = {
  connected: false,
  linkStatus: "reconnecting",
  startedAt: Date.now(),
  view: "home",
  self: null,
  device: null,
  battery: 0,
  contacts: [],
  channels: [],
  messages: [],
  nearby: [],
  selected: "",
  radio: null,
  packets: null,
  rfSamples: [],
  network: null,
  ultimate: null,
  ultimateHistory: [],
  ultimateSettings: null,
  historyNode: "",
};

let connection = new HttpPollingConnection();
let commandTail = Promise.resolve();
let syncing = false;
let recovering = false;
let recoveryAttempts = 0;
let unannouncedMessages = 0;
let advertSending = false;
let messageMenuTarget = null;
const favorites = createFavorites(window.localStorage);
const repeaterTools = createRepeaterTools({state, getConnection: () => connection, exclusive, toast, hex, refreshContacts, Constants, favorites, showView});
const contactMap = createContactMap({state, hex});
const keywordWatch = createKeywordWatch({toast, targetInfo});
let situationMap;
const secureComs = createSecureComs({state,getConnection:()=>connection,exclusive,hex,toast,onObject:(peer,value)=>situationMap.ingest(peer,value)});
situationMap = createSituationMap({state,secureComs,hex});
const commandDiscovery = createCommandDiscovery({state,getConnection:()=>connection,exclusive,hex,refreshContacts,secureComs});
const roomTools = createRoomTools();
const monitoring = createMonitoring({state,getConnection:()=>connection,exclusive,hex,Constants,addMessage,persistHistory,renderMessages,toast,onChange:()=>renderHome()});
const contactsTools = createContactsTools({state, getConnection: () => connection, exclusive, toast, hex, refreshContacts, selectTarget, showView, addMessage, Constants, favorites});
favorites.subscribe(() => { repeaterTools.render(); contactsTools.render(); });

function exclusive(task) {
  const result = commandTail.then(() => {
    let timer;
    const timeout = new Promise((_, reject) => {
      timer = setTimeout(() => {
        const error = new Error("Companion command timed out");
        error.rcc6CommandTimeout = true;
        reject(error);
      }, COMMAND_TIMEOUT_MS);
    });
    return Promise.race([Promise.resolve().then(task), timeout]).finally(() => clearTimeout(timer));
  });
  commandTail = result.catch(() => {});
  result.catch((error) => {
    if (error?.rcc6CommandTimeout || error?.rcc6TransportFailure) scheduleConnectionRecovery();
  });
  return result;
}

function scheduleConnectionRecovery() {
  if (recovering || recoveryAttempts >= MAX_RECOVERY_ATTEMPTS) {
    if (recoveryAttempts >= MAX_RECOVERY_ATTEMPTS) toast("Radio sync is paused. Tap the link status to retry.", "error");
    return;
  }
  recovering = true;
  recoveryAttempts += 1;
  setLink("reconnecting");
  toast(`Recovering companion link (${recoveryAttempts}/${MAX_RECOVERY_ATTEMPTS})`, "warn");
  setTimeout(async () => {
    const previous = connection;
    try {
      await previous.close();
      syncing = false;
      $("refresh-button").disabled = false;
      connection = new HttpPollingConnection();
      bindConnectionEvents(connection);
      await connection.connect();
    } catch {
      setLink("offline");
      toast("Companion recovery failed", "error");
    } finally {
      recovering = false;
    }
  }, 0);
}

function text(id, value) { $(id).textContent = value ?? "—"; }
function hex(bytes, length = bytes?.length ?? 0) { return bytes ? [...bytes.slice(0, length)].map((value) => value.toString(16).padStart(2, "0")).join("") : ""; }
function hashText(value) {
  let hash = 2166136261;
  for (let index = 0; index < value.length; index += 1) hash = Math.imul(hash ^ value.charCodeAt(index), 16777619);
  return (hash >>> 0).toString(16);
}
function historyKey() { return state.self?.publicKey ? `rcc6-history-${hex(state.self.publicKey)}` : ""; }
function pendingHistoryKey() { return `rcc6-history-pending-${connection.sessionId}`; }
function messageId(kind, key, timestamp, value) { return `${kind}:${key}:${timestamp}:${hashText(value)}`; }
function persistHistory() {
  const key = historyKey() || pendingHistoryKey();
  try { localStorage.setItem(key, JSON.stringify(state.messages.slice(-MAX_HISTORY_MESSAGES))); } catch { /* storage is optional */ }
}
function readHistory(key) {
  try {
    const saved = JSON.parse(localStorage.getItem(key) || "[]");
    if (!Array.isArray(saved)) return [];
    return saved.filter((item) => item && typeof item.id === "string" && typeof item.key === "string" && typeof item.text === "string")
      .slice(-MAX_HISTORY_MESSAGES)
      .map((item) => ({
        id: item.id.slice(0, 180), key: item.key.slice(0, 100), text: item.text.slice(0, MAX_MESSAGE_BYTES * 2),
        timestamp: Number.isFinite(item.timestamp) ? item.timestamp : 0,
        direction: item.direction === "out" ? "out" : "in",
        status: typeof item.status === "string" ? item.status.slice(0, 16) : "received",
        unread: Boolean(item.unread),
        snr: Number.isFinite(item.snr) ? item.snr : undefined,
        pathLen: Number.isInteger(item.pathLen) && item.pathLen >= 0 && item.pathLen <= 255 ? item.pathLen : undefined,
        floodRegion: typeof item.floodRegion === "string" ? item.floodRegion.slice(0, 40) : undefined,
        routeType: ["direct", "flood"].includes(item.routeType) ? item.routeType : undefined,
        authorPrefix: typeof item.authorPrefix === "string" && /^[0-9a-f]{8}$/.test(item.authorPrefix) ? item.authorPrefix : undefined,
        expectedAckCrc: Number.isInteger(item.expectedAckCrc) ? item.expectedAckCrc : undefined,
      }));
  } catch { return []; }
}
function loadHistoryForNode() {
  const key = historyKey();
  if (!key || state.historyNode === key) return;
  state.historyNode = key;
  const merged = [...readHistory(key), ...readHistory(pendingHistoryKey()), ...state.messages];
  const seen = new Set();
  state.messages = merged.filter((item) => !seen.has(item.id) && seen.add(item.id)).slice(-MAX_HISTORY_MESSAGES);
  try { localStorage.removeItem(pendingHistoryKey()); } catch { /* storage is optional */ }
  persistHistory();
}
function channelKey(channel) { return `ch:${channel.channelIdx}`; }
function mutedChannelId(key) {
  const channel = state.channels.find((item) => channelKey(item) === key);
  return channel && historyKey() ? `${channel.channelIdx}:${channel.name}` : "";
}
function channelScope(key) {
  const id = mutedChannelId(key); if (!id) return 'default';
  try { const values = JSON.parse(localStorage.getItem(`${historyKey()}:channel-scopes`) || '{}'); return typeof values[id] === 'string' ? values[id] : 'default'; } catch { return 'default'; }
}
function saveChannelScope(key, value) {
  const id = mutedChannelId(key); if (!id) return;
  try { const store = `${historyKey()}:channel-scopes`, values = JSON.parse(localStorage.getItem(store) || '{}'); values[id] = value; localStorage.setItem(store, JSON.stringify(values)); } catch { toast('Could not save channel scope', 'error'); }
}
function showChannelScope(key) {
  const scope = key.startsWith('ch:') ? channelScope(key) : 'default', select = $('region-select');
  select.value = [...select.options].some(o => o.value === scope && o.value !== 'custom') ? scope : 'custom';
  $('custom-region').hidden = select.value !== 'custom'; $('custom-region').value = select.value === 'custom' ? scope : '';
}
function channelIsMuted(key) {
  const id = mutedChannelId(key);
  if (!id) return false;
  try { const values = JSON.parse(localStorage.getItem(`${historyKey()}:muted-channels`) || "[]"); return Array.isArray(values) && values.includes(id); } catch { return false; }
}
function toggleChannelMute(key) {
  const id = mutedChannelId(key); if (!id) return;
  try {
    const store = `${historyKey()}:muted-channels`;
    const saved = JSON.parse(localStorage.getItem(store) || "[]");
    const values = new Set(Array.isArray(saved) ? saved.filter(v => typeof v === "string") : []);
    values.has(id) ? values.delete(id) : values.add(id);
    localStorage.setItem(store, JSON.stringify([...values]));
    renderMessages(); renderHome();
    toast(values.has(id) ? "Channel muted" : "Channel unmuted");
  } catch { toast("Could not save channel mute setting", "error"); }
}
function contactKey(contact) { return `dm:${hex(contact.publicKey)}`; }
function initials(name = "?") { return name.trim().split(/\s+/).slice(0, 2).map((part) => part[0]).join("").toUpperCase() || "?"; }
function formatNumber(value) { return Number.isFinite(value) ? value.toLocaleString() : "—"; }
function formatClock(epochSecs) { return epochSecs ? new Date(epochSecs * 1000).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }) : "now"; }
function formatAge(epochSecs) {
  if (!epochSecs) return "just now";
  const seconds = Math.max(0, Math.floor(Date.now() / 1000) - epochSecs);
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}
function formatSession() {
  const minutes = Math.floor((Date.now() - state.startedAt) / 60000);
  return minutes < 1 ? "Now" : minutes < 60 ? `${minutes}m` : `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}
function radioFrequency(value) { return Number.isFinite(value) ? (value / 1000).toFixed(3) : "—"; }
function radioBandwidth(value) { return Number.isFinite(value) ? (value / 1000).toFixed(value % 1000 ? 1 : 0) : "—"; }
function signalQuality(rssi) { return rssi >= -80 ? "Excellent" : rssi >= -95 ? "Good" : rssi >= -110 ? "Fair" : "Weak"; }

function canvasContext(canvas, minimumHeight = 120) {
  if (!canvas) return null;
  const width = Math.max(280, canvas.clientWidth || 0);
  const height = Math.max(minimumHeight, canvas.clientHeight || 0);
  const ratio = Math.min(devicePixelRatio || 1, 2);
  canvas.width = Math.round(width * ratio);
  canvas.height = Math.round(height * ratio);
  const context = canvas.getContext("2d");
  context.scale(ratio, ratio);
  context.clearRect(0, 0, width, height);
  return { context, width, height };
}

function drawActivityBars(canvas, rows, limit = 24) {
  const surface = canvasContext(canvas, 110);
  if (!surface) return 0;
  const { context, width, height } = surface;
  const ordered = [...(rows || [])].reverse().slice(-limit);
  context.strokeStyle = "rgba(141,152,167,.11)";
  context.lineWidth = 1;
  for (let row = 1; row < 4; row += 1) {
    const y = Math.round(row * (height - 24) / 4) + .5;
    context.beginPath(); context.moveTo(0, y); context.lineTo(width, y); context.stroke();
  }
  if (!ordered.length) return 0;
  const values = ordered.map((row) => ({ rx: Number(row[1]) || 0, tx: Number(row[2]) || 0, fail: Number(row[3]) || 0 }));
  const maximum = Math.max(1, ...values.flatMap((row) => [row.rx, row.tx, row.fail]));
  const group = width / values.length;
  const barWidth = Math.max(1.5, Math.min(8, group * .22));
  values.forEach((row, index) => {
    const x = index * group + (group - barWidth * 3) / 2;
    [[row.rx, "#44f08a"], [row.tx, "#42a5ff"], [row.fail, "#ff5468"]].forEach(([value, color], series) => {
      const barHeight = Math.max(value ? 2 : 0, value / maximum * (height - 26));
      context.fillStyle = color;
      context.globalAlpha = .84;
      context.fillRect(x + series * barWidth, height - 12 - barHeight, Math.max(1, barWidth - 1), barHeight);
    });
  });
  context.globalAlpha = 1;
  return values.reduce((total, row) => total + row.rx + row.tx, 0);
}

function element(tag, className, content) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (content != null) node.textContent = content;
  return node;
}

function toast(message, kind = "ok") {
  const item = element("div", `toast ${kind}`);
  item.append(element("i"), element("span", "", message));
  $("toasts").append(item);
  setTimeout(() => item.classList.add("out"), 3200);
  setTimeout(() => item.remove(), 3500);
}

function setLink(status) {
  const online = status === "connected";
  if (!online && state.connected) { $("setting-path-hash").value=""; text("path-hash-status", "Radio disconnected; read the current setting after reconnecting."); }
  state.connected = online;
  state.linkStatus = status;
  const label = online ? "Connected" : status === "reconnecting" ? "Reconnecting" : status === "busy" ? "Another tab active" : "Offline";
  text("link-label", label);
  text("side-status", label);
  text("hero-link", label);
  $("connect-button").className = `link-chip ${status}`;
  $("side-dot").className = `status-dot ${status}`;
  updateAdvertControls();
  $("home-sync").disabled = !online;
  secureComs.refresh();
  contactMap.schedule();
}

function targetInfo(key) {
  if (key.startsWith("ch:")) {
    const channel = state.channels.find((item) => channelKey(item) === key);
    return channel ? { key, name: channel.name?.startsWith("#") ? channel.name : `#${channel.name || `Channel ${channel.channelIdx}`}`, subtitle: "Mesh channel", avatar: "#", channel } : null;
  }
  if (key.startsWith("room:")) {
    const room = state.contacts.find((item) => item.type === 3 && `room:${hex(item.publicKey)}` === key);
    return room ? { key, name: room.advName || "Unnamed room", subtitle: "Room server", avatar: "R", contact: room, room } : null;
  }
  const contact = state.contacts.find((item) => contactKey(item) === key);
  return contact ? { key, name: contact.advName || "Unnamed contact", subtitle: `Direct · ${hex(contact.publicKey, 4)}`, avatar: initials(contact.advName), contact } : null;
}

function targetForContactPrefix(prefix) {
  const matches = state.contacts.filter((item) => prefix.every((byte, index) => item.publicKey[index] === byte));
  const contact = matches.length === 1 ? matches[0] : null;
  return contact ? (contact.type === 3 ? `room:${hex(contact.publicKey)}` : contactKey(contact)) : `dm-prefix:${hex(prefix)}`;
}

function normalizeTargets() {
  if (state.selected && targetInfo(state.selected)) return;
  state.selected = state.channels[0] ? channelKey(state.channels[0]) : "";
  showChannelScope(state.selected);
}

function addMessage(message) {
  const item = { id: `${Date.now()}-${Math.random()}`, ...message };
  if (state.messages.some((existing) => existing.id === item.id)) return null;
  state.messages.push(item);
  if (state.messages.length > MAX_HISTORY_MESSAGES) state.messages.splice(0, state.messages.length - MAX_HISTORY_MESSAGES);
  persistHistory();
  keywordWatch.check(item, {muted: channelIsMuted(item.key)});
  renderMessages();
  renderHome();
  return item;
}

function ingestWaiting(item) {
  if (item.contactMessage) {
    const message = item.contactMessage;
    if (secureComs.ingest(message)) return 0;
    if (repeaterTools.handleMessage(message)) return 0;
    const key = targetForContactPrefix(message.pubKeyPrefix);
    const added = addMessage({ id: messageId("dm", hex(message.pubKeyPrefix), message.senderTimestamp, message.authorPrefix ? `${hex(message.authorPrefix)}:${message.text}` : message.text), key, text: message.text, authorPrefix: message.authorPrefix ? hex(message.authorPrefix) : undefined, timestamp: message.senderTimestamp, direction: "in", status: "received", unread: document.hidden || state.view !== "messages" || state.selected !== key, snr: message.snr, pathLen: message.pathLen });
    if (added && key.startsWith("room:")) roomTools.noteMessage(key);
    return added ? 1 : 0;
  }
  if (item.channelMessage) {
    const message = item.channelMessage;
    if (commandDiscovery.ingest(message)) return 0;
    const key = `ch:${message.channelIdx}`;
    const added = addMessage({ id: messageId("channel", key, message.senderTimestamp, message.text), key, text: message.text, timestamp: message.senderTimestamp, direction: "in", status: "received", unread: document.hidden || state.view !== "messages" || state.selected !== key, snr: message.snr, pathLen: message.pathLen });
    return added && !channelIsMuted(key) ? 1 : 0;
  }
  return 0;
}

function refreshNearbyFromContacts() {
  state.nearby = [...state.contacts].filter((item) => item.lastAdvert).sort((a, b) => b.lastAdvert - a.lastAdvert).slice(0, 24);
}

async function refreshContacts() {
  const contacts = await connection.getContacts();
  state.contacts = contacts.sort((a, b) => (b.lastAdvert || 0) - (a.lastAdvert || 0));
  let historyChanged = false;
  for (const message of state.messages) {
    if (!message.key.startsWith("dm-prefix:")) continue;
    const prefix = message.key.slice("dm-prefix:".length);
    const matches = state.contacts.filter((item) => hex(item.publicKey).startsWith(prefix));
    if (matches.length === 1) { const contact = matches[0]; message.key = contact.type === 3 ? `room:${hex(contact.publicKey)}` : contactKey(contact); historyChanged = true; }
  }
  if (historyChanged) persistHistory();
  refreshNearbyFromContacts();
  normalizeTargets();
  renderAll();
}

async function syncMessages() {
  let added = 0;
  while (true) {
    const waiting = await connection.syncNextMessage();
    if (!waiting) break;
    added += ingestWaiting(waiting);
  }
  added += unannouncedMessages;
  unannouncedMessages = 0;
  if (added) toast(`${added} new message${added === 1 ? "" : "s"}`, "warn");
}

async function refreshStats() {
  if (!state.connected) return;
  try {
    const battery = await connection.getBatteryVoltage();
    state.battery = battery.batteryMilliVolts;
  } catch { /* optional */ }
  try {
    const radio = await connection.getStatsRadio();
    state.radio = radio.data;
    if (Number.isFinite(state.radio.lastRssi)) state.rfSamples.push(state.radio.lastRssi);
    state.rfSamples = state.rfSamples.slice(-36);
  } catch { /* optional */ }
  try { state.packets = (await connection.getStatsPackets()).data; } catch { /* optional */ }
  renderAll();
}

async function refreshNetworkStatus() {
  try {
    const response = await fetch("/api/network", {
      headers: { Accept: "application/json", "X-RCC6-Session": connection.sessionId },
      cache: "no-store",
      credentials: "same-origin",
    });
    if (!response.ok) throw new Error(`Network status failed (${response.status})`);
    const data = await response.json();
    if (data.mode !== "ap" && data.mode !== "station") throw new Error("Invalid network mode");
    state.network = {
      mode: data.mode,
      ssid: typeof data.ssid === "string" ? data.ssid : "",
      ip: typeof data.ip === "string" ? data.ip : "",
      fallback: Boolean(data.fallback),
      loginUser: typeof data.loginUser === "string" ? data.loginUser : "meshcore",
      loginKey: typeof data.loginKey === "string" ? data.loginKey : "",
    };
  } catch { /* network configuration endpoint is optional on older builds */ }
  renderNetwork();
}

async function ultimateFetch(path, options = {}) {
  const response = await fetch(path, {
    cache: "no-store",
    credentials: "same-origin",
    ...options,
    headers: { Accept: "application/json", ...(options.headers || {}) },
  });
  if (!response.ok) throw new Error(`Ultimate API failed (${response.status})`);
  return response;
}

async function refreshUltimate() {
  try {
    const [status, history, settings] = await Promise.all([
      ultimateFetch("/api/ultimate/status").then((response) => response.json()),
      ultimateFetch("/api/ultimate/history?limit=20").then((response) => response.json()),
      ultimateFetch("/api/ultimate/settings").then((response) => response.json()),
    ]);
    state.ultimate = status;
    state.ultimateHistory = Array.isArray(history.records) ? history.records : [];
    state.ultimateSettings = settings;
  } catch { /* non-Ultimate firmware remains compatible with the standard WebUI */ }
  renderUltimate();
}

async function setNetwork(fields) {
  if (!connection.ready || !connection.ownsLease()) throw new Error("RCC6 companion link is not ready");
  const response = await fetch("/api/network", {
    method: "POST",
    headers: {
      "Content-Type": "application/x-www-form-urlencoded",
      "X-RCC6-Session": connection.sessionId,
    },
    body: new URLSearchParams(fields),
    cache: "no-store",
    credentials: "same-origin",
  });
  if (!response.ok) throw new Error(`Network update failed (${response.status})`);
  return response.json();
}

// Room logins and binary requests use the companion's RTC, while chat uses
// the PC clock. A stale RTC makes the room reject requests as replayed after
// a chat message. MeshCore only permits setting its clock forwards.
let radioClockCorrection = "";
async function syncRadioClock() {
  const clock = await connection.getDeviceTime();
  const radioTime = Number(clock?.epochSecs);
  const pcTime = Math.floor(Date.now() / 1000);
  if (!Number.isInteger(radioTime) || radioTime < 0 || pcTime < 1577836800 || pcTime > 0xffffffff) throw Error("Could not verify the radio clock.");
  if (radioTime > pcTime + 60) { text("radio-clock-status", "Radio clock is ahead of the PC clock; check the PC's date and time."); throw Error("Radio clock is ahead of the PC clock; check the PC's date and time."); }
  if (radioTime < pcTime - 2) {
    await connection.setDeviceTime(pcTime);
    radioClockCorrection = ` Radio clock was updated from ${new Date(radioTime * 1000).toLocaleString()}.`;
  }
  text("radio-clock-status", `Radio clock is in sync with the PC.${radioClockCorrection}`);
}

async function syncAll() {
  if (syncing) return;
  syncing = true;
  $("refresh-button").disabled = true;
  try {
    await refreshNetworkStatus();
    try { state.device = await connection.deviceQuery(Constants.SupportedCompanionProtocolVersion); } catch { /* older firmware */ }
    state.self = await connection.getSelfInfo(6000);
    try { await syncRadioClock(); } catch { toast("Radio clock could not be synchronised. Check your PC's date and time.", "error"); }
    loadHistoryForNode();
    await refreshContacts();
    try { state.channels = (await connection.getChannels()).filter((item) => item.name); } catch { state.channels = []; }
    normalizeTargets();
    await syncMessages();
    await refreshStats();
    await refreshUltimate();
    renderAll();
    fillMeshSettings();
  } finally {
    syncing = false;
    $("refresh-button").disabled = false;
  }
}

function renderHome() {
  const self = state.self;
  text("node-name", self?.name || "RADIO");
  text("node-id", self?.publicKey ? `ID ${hex(self.publicKey, 8)}…` : "Waiting for companion identity…");
  text("hero-contacts", state.self ? state.contacts.length : "—");
  text("hero-uptime", formatSession());
  const unread = state.messages.filter((item) => item.unread && !channelIsMuted(item.key));
  text("home-unread", unread.length);
  text("nav-unread", unread.length);
  $("nav-unread").hidden = !unread.length;
  const latest = unread.at(-1);
  const preview = $("message-preview");
  preview.replaceChildren();
  if (latest) {
    const target = targetInfo(latest.key);
    preview.className = "preview";
    preview.append(element("strong", "", target?.name || "New message"), element("span", "", latest.text));
  } else {
    preview.className = "preview empty";
    preview.textContent = "No unread messages";
  }
  const nearby = state.nearby[0];
  const nearbyPreview = $("nearby-preview");
  nearbyPreview.replaceChildren();
  if (nearby) {
    nearbyPreview.className = "preview";
    nearbyPreview.append(element("strong", "", nearby.advName || "Unnamed node"), element("span", "", `${formatAge(nearby.lastAdvert)} · ${hex(nearby.publicKey, 4)}`));
  } else {
    nearbyPreview.className = "preview empty";
    nearbyPreview.textContent = "Waiting for adverts";
  }
  const rssi = state.radio?.lastRssi;
  text("home-rssi", Number.isFinite(rssi) ? `${rssi} dBm` : "— dBm");
  text("home-snr", Number.isFinite(state.radio?.lastSnr) ? `SNR ${state.radio.lastSnr.toFixed(1)}` : "SNR —");
  text("home-frequency", self?.radioFreq ? `${radioFrequency(self.radioFreq)} MHz` : "— MHz");
  text("home-packets", state.packets ? `${formatNumber(state.packets.recv)} packets` : "— packets");
  $("radio-meter-fill").style.width = Number.isFinite(rssi) ? `${Math.max(4, Math.min(100, (rssi + 125) * 2))}%` : "0";
  renderOverviewMonitoring();
  renderDashboardAnalytics();
}

function renderOverviewMonitoring() {
  const root=$("home-monitors"),items=monitoring.summary(),repeaters=items.filter(item=>item.contact?.type===2),others=items.filter(item=>item.contact?.type!==2),ordered=[...repeaters,...others];
  root.replaceChildren();text("home-monitor-count", `${items.length} monitored`);
  if(!items.length){root.append(element("p","quiet","No monitored nodes yet. Add repeaters or contacts in Monitoring."));return;}
  const max=window.innerHeight<800?2:4;
  for(const item of ordered.slice(0,max)){
    const row=element("button","home-monitor-row");row.type="button";row.title="Open Monitoring";row.onclick=()=>showView("monitoring");
    const name=item.contact?.advName||`Unavailable node ${item.key.slice(0,8)}`;
    const results=Object.entries(item.results||{}).filter(([,r])=>r&&Number.isFinite(r.time)).sort((a,b)=>b[1].time-a[1].time);
    const last=results[0],value=last?`${last[0][0].toUpperCase()+last[0].slice(1)}: ${last[1].text} · ${formatAge(Math.floor(last[1].time/1000))}`:'No checks yet';
    const telemetry=item.results?.telemetry,voltage=telemetry?.data?.values?.find(v=>v.channel===1&&v.type===116&&Number.isFinite(v.number));
    const title=element("strong","",name),detail=element("span","",item.contact?value:'Not in radio contacts · checks paused');
    const right=element("small","",voltage?`${voltage.number.toFixed(2)} V`:(Object.values(item.enabled).some(Boolean)?'Scheduled':'Manual'));
    const description=element("div","home-monitor-info");description.append(title,detail);row.append(description,right);root.append(row);
  }
  if(items.length>max)root.append(element("p","quiet",`+ ${items.length-max} more in Monitoring`));
}

function renderDashboardAnalytics() {
  const ultimate = state.ultimate;
  const received = ultimate?.rx ?? state.packets?.recv;
  const sent = ultimate?.tx ?? state.packets?.sent;
  text("dashboard-rx", formatNumber(received));
  text("dashboard-tx", formatNumber(sent));
  const outgoing = [...state.messages].reverse().find(item => item.direction === "out");
  text("dashboard-delivery", outgoing ? outgoing.status.toUpperCase() : "—");
  text("dashboard-delivery-note", outgoing ? (targetInfo(outgoing.key)?.name || "Last message") : "No local send");
  text("dashboard-history", formatNumber(state.messages.length));
  text("dashboard-history-note", "messages stored locally");
  const hours = Array.from({length: 24}, (_, i) => [i, 0, 0, 0]);
  const currentHour = Math.floor(Date.now() / 3600000);
  for (const item of state.messages) {
    const age = currentHour - Math.floor(item.timestamp / 3600);
    if (!Number.isFinite(age) || age < 0 || age >= 24) continue;
    const row = hours[age];
    if (item.direction === "in") row[1]++;
    else if (item.status === "failed") row[3]++;
    else row[2]++;
  }
  const total = drawActivityBars($("dashboard-activity-chart"), hours, 24);
  const failed = hours.reduce((sum, row) => sum + row[3], 0);
  text("dashboard-activity-total", `${formatNumber(total + failed)} local message${total + failed === 1 ? "" : "s"}`);
  drawSignalHistogram($("dashboard-signal-chart"), state.rfSamples);
  renderNearbyBars();
}

function drawSignalHistogram(canvas, samples = []) {
  const surface = canvasContext(canvas, 138);
  if (!surface) return;
  const { context, width, height } = surface;
  const values = samples.filter(Number.isFinite).slice(-60);
  text("dashboard-signal-note", values.length ? `${values.length} recent sample${values.length === 1 ? "" : "s"}` : "Waiting for packets");
  context.clearRect(0, 0, width, height);
  context.strokeStyle = "rgba(141,152,167,.14)";
  [0.25, 0.5, 0.75].forEach((part) => { context.beginPath(); context.moveTo(0, height * part); context.lineTo(width, height * part); context.stroke(); });
  const edges = [-130, -115, -105, -95, -85, -70];
  const counts = edges.slice(0, -1).map((low, index) => values.filter((value) => value >= low && value < edges[index + 1]).length);
  const max = Math.max(1, ...counts); const gap = 8; const barWidth = (width - gap * (counts.length - 1)) / counts.length;
  counts.forEach((count, index) => {
    const barHeight = Math.max(count ? 5 : 1, count / max * (height - 28));
    const gradient = context.createLinearGradient(0, height - barHeight, 0, height);
    gradient.addColorStop(0, index >= 3 ? "#44f08a" : index >= 2 ? "#39e7ff" : "#ff8a3d"); gradient.addColorStop(1, "rgba(36,87,255,.28)");
    context.fillStyle = gradient; context.fillRect(index * (barWidth + gap), height - barHeight - 16, barWidth, barHeight);
    context.fillStyle = "rgba(245,248,251,.78)"; context.font = "9px system-ui, sans-serif"; context.textAlign = "center";
    context.fillText(String(count), index * (barWidth + gap) + barWidth / 2, height - 3);
  });
  context.textAlign = "left";
}

function routeSummary(contact) {
  const raw = Number(contact?.outPathLen); const packed = Number.isFinite(raw) ? raw & 0xff : 0xff;
  const hops = packed === 0xff ? null : packed & 0x3f;
  return { hops, label: hops === null ? "route unknown" : hops === 0 ? "direct" : `${hops} hop${hops === 1 ? "" : "s"}` };
}

function renderNearbyBars() {
  const host = $("dashboard-nearby-bars"); host.replaceChildren();
  const now = Math.floor(Date.now() / 1000); const nodes = state.nearby.slice(0, 6);
  if (!nodes.length) { host.append(element("p", "empty-state", "Waiting for node adverts.")); return; }
  nodes.forEach((contact) => {
    const age = Math.max(0, now - Number(contact.lastAdvert || now));
    const freshness = Math.max(5, 100 - Math.min(100, age / 216));
    const row = element("div", "nearby-bar"); const track = element("span", "bar-track"); const fill = document.createElement("i");
    fill.style.width = `${freshness}%`; track.append(fill);
    row.append(element("strong", "", contact.advName || "Unnamed node"), track, element("span", "", `${routeSummary(contact).label} · ${formatAge(contact.lastAdvert)}`));
    host.append(row);
  });
}

let messageTab = "direct";
const recentAcks = [];
let renderedMessageKey = null;
function messageRouteDetails(message) {
  let hops = "Hops: not reported";
  if (message.direction === "in" && Number.isInteger(message.pathLen) && message.pathLen >= 0 && message.pathLen <= 255) {
    if (message.pathLen === 255) hops = "Direct route · hops not reported";
    else { const count = message.pathLen & 63; hops = `Hops: ${count}`; }
  }
  let region = "Region: not reported";
  if (message.direction === "out" && message.routeType === "direct") region = "Region: not used (direct route)";
  else if (message.direction === "out" && message.routeType === "flood" && message.floodRegion) region = `Region: ${message.floodRegion}`;
  return `${hops} · ${region}`;
}
function renderMessages() {
  normalizeTargets();
  const unreadByTab = {direct:0,channels:0,rooms:0};
  for (const item of state.messages) {
    if (!item.unread || channelIsMuted(item.key)) continue;
    const tab = item.key.startsWith("ch:") ? "channels" : item.key.startsWith("room:") ? "rooms" : "direct";
    unreadByTab[tab]++;
  }
  for (const tab of ["direct","channels","rooms"]) {
    const badge = $(`unread-${tab}`), count = unreadByTab[tab];
    badge.textContent = count > 99 ? "99+" : String(count);
    badge.hidden = count === 0;
    $(`messages-${tab}`).setAttribute("aria-label", `${tab === "direct" ? "Direct messages" : tab === "channels" ? "Channels" : "Rooms"}${count ? `, ${count} unread` : ", no unread"}`);
  }
  const targets = messageTab === "rooms" ? roomTools.targets() : messageTab === "channels"
    ? state.channels.map((channel) => ({ key: channelKey(channel), name: channel.name.startsWith("#") ? channel.name : `#${channel.name}`, subtitle: "Channel", avatar: "#" }))
    : state.contacts.filter(contact => contact.type === 1 && (state.selected === contactKey(contact) || state.messages.some(item => item.key === contactKey(contact)))).map(contact => ({ key: contactKey(contact), name: contact.advName || "Unnamed contact", subtitle: "Direct", avatar: initials(contact.advName) }));
  $("messages-direct").setAttribute("aria-pressed", String(messageTab === "direct"));
  $("messages-channels").setAttribute("aria-pressed", String(messageTab === "channels"));
  $("messages-rooms").setAttribute("aria-pressed", String(messageTab === "rooms"));
  $("channel-add-panel").hidden = messageTab !== "channels";
  roomTools.render(messageTab === "rooms");
  const query = $("message-search").value.trim().toLocaleLowerCase();
  const visibleTargets = (query ? targets.filter(target => matchesConversation(target, query)) : [...targets]);
  const threadActivity = key => {
    const messages = state.messages.filter(item => item.key === key);
    return {unread: !channelIsMuted(key) && messages.some(item => item.unread), latest: messages.at(-1)?.timestamp || 0};
  };
  visibleTargets.sort((a,b) => {const x=threadActivity(a.key), y=threadActivity(b.key); return Number(y.unread)-Number(x.unread) || y.latest-x.latest;});
  text("thread-count", query ? `${visibleTargets.length} / ${targets.length}` : targets.length);
  const threadList = $("thread-list");
  threadList.replaceChildren();
  if (!visibleTargets.length) threadList.append(element("p", "empty-state", targets.length ? "No contacts or channels match your search." : messageTab === "direct" ? "No direct conversations yet. Start one from Contacts." : messageTab === "rooms" ? "Add a room server from the list above." : "No channels available."));
  for (const target of visibleTargets) {
    const messages = state.messages.filter((item) => item.key === target.key);
    const latest = messages.at(-1);
    const unread = messages.filter((item) => item.unread).length;
    const button = element("button", `thread${state.selected === target.key ? " active" : ""}`);
    button.type = "button";
    const avatar = element("span", "avatar", target.avatar);
    const copy = element("span", "thread-copy");
    copy.append(element("strong", "", target.name), element("small", "", (channelIsMuted(target.key) ? "Muted · " : "") + (latest?.text || target.subtitle)));
    const meta = unread && !channelIsMuted(target.key) ? element("span", "thread-badge", unread) : element("time", "thread-time", latest ? formatClock(latest.timestamp) : "");
    button.append(avatar, copy, meta);
    button.addEventListener("click", () => selectTarget(target.key));
    threadList.append(button);
  }

  const select = $("target-select");
  select.replaceChildren(new Option("Choose recipient", ""));
  if (state.channels.length) {
    const group = document.createElement("optgroup"); group.label = "Channels";
    for (const channel of state.channels) group.append(new Option(channel.name.startsWith("#") ? channel.name : `#${channel.name}`, channelKey(channel)));
    select.append(group);
  }
  if (state.contacts.length) {
    const group = document.createElement("optgroup"); group.label = "Direct contacts";
    for (const contact of state.contacts.filter(c => c.type === 1 && (state.selected === contactKey(c) || state.messages.some(item => item.key === contactKey(c))))) group.append(new Option(contact.advName || "Unnamed contact", contactKey(contact)));
    select.append(group);
  }
  const roomTargets = roomTools.targets();
  if (roomTargets.length) { const group = document.createElement("optgroup"); group.label = "Room servers"; for (const room of roomTargets) group.append(new Option(room.name, room.key)); select.append(group); }
  select.value = state.selected;
  const info = targetInfo(state.selected);
  text("conversation-title", info?.name || "Choose a conversation");
  const muted = info?.channel && channelIsMuted(state.selected);
  text("conversation-subtitle", info?.channel ? `Mesh channel · scope ${channelScope(state.selected) === "default" ? "radio default" : channelScope(state.selected)}${muted ? " · muted" : ""}` : info?.subtitle || "Channel or direct contact");
  const muteButton = $("channel-mute");
  muteButton.hidden = !info?.channel;
  muteButton.disabled = !historyKey();
  muteButton.textContent = muted ? "Unmute channel" : "Mute channel";
  muteButton.setAttribute("aria-pressed", String(Boolean(muted)));
  muteButton.title = "Mute notifications and unread badges; messages and word matches are still saved.";
  text("conversation-avatar", info?.avatar || "#");
  const list = $("message-list");
  const messages = state.messages.filter((item) => item.key === state.selected);
  if (state.selected.startsWith("room:")) messages.sort((a, b) => a.timestamp - b.timestamp);
  const conversationChanged = renderedMessageKey !== state.selected;
  const nearBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 40;
  const selection = window.getSelection();
  const selecting = selection && !selection.isCollapsed && list.contains(selection.anchorNode);
  if (conversationChanged) { list.replaceChildren(); renderedMessageKey = state.selected; }
  const existing = new Map([...list.querySelectorAll(':scope > .bubble')].map(b => [b.dataset.messageId, b]));
  const wanted = new Set(messages.map(message => String(message.id)));
  for (const child of [...list.children]) if (!child.classList.contains('bubble') || !wanted.has(child.dataset.messageId)) child.remove();
  if (!messages.length && !list.children.length) list.append(element("div", "welcome-bubble", info ? `No messages with ${info.name} yet.` : "Select a channel or contact to start talking across the mesh."));
  let next = list.firstElementChild;
  for (const message of messages) {
    let bubble = existing.get(String(message.id));
    if (!bubble) {
      bubble = element("article", "bubble"); bubble.dataset.messageId = String(message.id);
      bubble.append(element("p", "message-body"), element("small", "message-meta"), element("small", "message-delivered"), element("small", "message-route"));
      bubble.addEventListener("contextmenu", (event) => openMessageMenu(event, bubble._message));
    }
    bubble._message = message;
    bubble.className = `bubble ${message.direction === "out" ? "out" : "in"} ${message.status === "failed" ? "failed" : ""}`;
    let author = bubble.querySelector('.room-author');
    if (message.authorPrefix) {
      const matches = state.contacts.filter((contact) => hex(contact.publicKey).startsWith(message.authorPrefix));
      const name = matches.length === 1 ? (matches[0].advName || `Author ${message.authorPrefix}`) : `Author ${message.authorPrefix}`;
      if (!author) { author = element('strong', 'room-author'); bubble.prepend(author); }
      if (author.textContent !== name) author.textContent = name;
    } else if (author) author.remove();
    const body = bubble.querySelector('.message-body');
    if (body.textContent !== message.text) body.textContent = message.text;
    const status = message.direction === "out" ? (message.status === "delivered" ? "" : message.status === "queued" ? (message.key.startsWith("ch:") ? " · Queued · no delivery confirmation" : " · Queued · awaiting confirmation") : ` · ${message.status}`) : Number.isFinite(message.snr) ? ` · SNR ${message.snr.toFixed(1)}` : "";
    const meta = bubble.querySelector('.message-meta'), metaText = `${formatClock(message.timestamp)}${status}`;
    if (meta.textContent !== metaText) meta.textContent = metaText;
    const delivered = bubble.querySelector('.message-delivered');
    delivered.hidden = !(message.direction === "out" && !message.key.startsWith("ch:") && message.status === "delivered");
    const deliveryText = message.key.startsWith("room:") ? "✓ Delivered to room server" : "✓ Delivered";
    if (delivered.textContent !== deliveryText) delivered.textContent = deliveryText;
    const route = bubble.querySelector('.message-route'), routeText = messageRouteDetails(message);
    if (route.textContent !== routeText) route.textContent = routeText;
    if (bubble !== next) list.insertBefore(bubble, next);
    next = bubble.nextElementSibling;
  }
  if ((conversationChanged || nearBottom) && !selecting) requestAnimationFrame(() => { list.scrollTop = list.scrollHeight; });
  $("message-input").disabled = !info || !state.connected;
  $("composer").querySelector("button[type=submit]").disabled = !info || !state.connected;
}

function selectTarget(key) {
  closeMessageMenu();
  messageTab = key.startsWith("ch:") ? "channels" : key.startsWith("room:") ? "rooms" : "direct";
  state.selected = key;
  showChannelScope(key);
  for (const message of state.messages) if (message.key === key) message.unread = false;
  persistHistory();
  renderMessages();
  renderHome();
}

function renderRadio() {
  const self = state.self;
  const radio = state.radio;
  const packets = state.packets;
  text("radio-rssi", Number.isFinite(radio?.lastRssi) ? radio.lastRssi : "—");
  text("radio-quality", Number.isFinite(radio?.lastRssi) ? signalQuality(radio.lastRssi) : "Waiting for sample");
  text("radio-snr", Number.isFinite(radio?.lastSnr) ? `SNR ${radio.lastSnr.toFixed(1)} dB` : "SNR —");
  text("radio-noise", Number.isFinite(radio?.noiseFloor) ? `Noise ${radio.noiseFloor} dBm` : "Noise —");
  text("radio-frequency", radioFrequency(self?.radioFreq));
  text("radio-bandwidth", radioBandwidth(self?.radioBw));
  text("radio-spreading", self?.radioSf ? `SF${self.radioSf}` : "—");
  text("radio-coding", self?.radioCr ? `CR 4/${self.radioCr}` : "CR —");
  text("radio-power", Number.isFinite(self?.txPower) ? self.txPower : "—");
  text("packet-received", formatNumber(packets?.recv));
  text("packet-sent", formatNumber(packets?.sent));
  text("packet-direct", formatNumber(packets?.nRecvDirect));
  text("packet-flood", formatNumber(packets?.nRecvFlood));
  const errors = packets?.nRecvErrors ?? 0;
  text("packet-errors", `${formatNumber(errors)} error${errors === 1 ? "" : "s"}`);
  $("packet-errors").className = errors ? "error" : "quiet";
  if (state.view === "radio") requestAnimationFrame(() => { drawSignalChart(); drawRadioActivity(); });
}

function drawRadioActivity() {
  const total = drawActivityBars($("radio-activity-chart"), state.ultimate?.hours, 72);
  text("radio-activity-total", total ? `${formatNumber(total)} packets shown` : "No samples yet");
}

function drawSignalChart() {
  const canvas = $("signal-chart");
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  if (width < 20 || height < 20) return;
  const ratio = Math.min(devicePixelRatio || 1, 2);
  canvas.width = Math.round(width * ratio); canvas.height = Math.round(height * ratio);
  const context = canvas.getContext("2d"); context.scale(ratio, ratio);
  context.clearRect(0, 0, width, height);
  context.strokeStyle = "rgba(141,152,167,.12)"; context.lineWidth = 1;
  for (let row = 1; row < 4; row += 1) { context.beginPath(); context.moveTo(0, row * height / 4); context.lineTo(width, row * height / 4); context.stroke(); }
  const samples = state.rfSamples.length > 1 ? state.rfSamples : [-120, -120];
  context.beginPath();
  samples.forEach((sample, index) => {
    const x = index * width / Math.max(1, samples.length - 1);
    const y = height - Math.max(0, Math.min(1, (sample + 130) / 60)) * height;
    index ? context.lineTo(x, y) : context.moveTo(x, y);
  });
  context.strokeStyle = "#44f08a"; context.lineWidth = 2; context.shadowColor = "#44f08a"; context.shadowBlur = 8; context.stroke();
}

function renderNetwork() {
  const network = state.network;
  const busy = state.linkStatus === "busy";
  text("network-login-user", network?.loginUser || "meshcore");
  text("network-login-key", network?.loginKey || "unavailable");
  if (!network) {
    text("network-title", "Set up local Wi-Fi");
    text("network-summary", "Use your 2.4 GHz home network instead of staying on the setup hotspot.");
    text("network-current", "Network status unavailable");
    text("more-network-title", "Radio network");
    text("ap-address", "Current mode and IP are unavailable");
    $("network-setup-button").textContent = "Set up local Wi-Fi";
  } else if (network.mode === "station") {
    text("network-title", `Connected to ${network.ssid || "local Wi-Fi"}`);
    text("network-summary", `${network.ip || "DHCP address pending"}${network.fallback ? " · fallback active" : " · ready on your local network"}`);
    text("network-current", `${network.ssid || "Local Wi-Fi"} · ${network.ip || "DHCP pending"}`);
    text("more-network-title", "Local Wi-Fi");
    text("ap-address", `${network.ssid || "Station mode"} · ${network.ip || "DHCP pending"}`);
    $("network-setup-button").textContent = "Change local Wi-Fi";
  } else {
    text("network-title", network.fallback ? "Local Wi-Fi needs attention" : "Set up local Wi-Fi");
    text("network-summary", `${network.ssid || "setup AP"} · ${network.ip || "192.168.4.1"}${network.fallback ? " · station fallback" : ""}`);
    text("network-current", `${network.ssid || "setup AP"} · ${network.ip || "192.168.4.1"}`);
    text("more-network-title", network.fallback ? "Fallback setup AP" : "Setup AP");
    text("ap-address", `${network.ssid || "Setup hotspot"} · ${network.ip || "192.168.4.1"}`);
    $("network-setup-button").textContent = "Set up local Wi-Fi";
  }
  $("network-setup-button").disabled = busy;
  $("return-ap-button").hidden = network?.mode !== "station";
  $("return-ap-button").disabled = busy;
}

function openNetworkDialog() {
  renderNetwork();
  $("network-form").hidden = false;
  $("network-result").hidden = true;
  $("network-ssid").value = state.network?.mode === "station" ? state.network.ssid : "";
  $("network-password").value = "";
  $("network-password").type = "password";
  $("network-reveal").textContent = "Show";
  $("network-reveal").setAttribute("aria-pressed", "false");
  const dialog = $("network-dialog");
  if (dialog.showModal) dialog.showModal(); else dialog.setAttribute("open", "");
  $("network-ssid").focus();
}

function closeNetworkDialog() {
  const dialog = $("network-dialog");
  if (dialog.close) dialog.close(); else dialog.removeAttribute("open");
}

function showNetworkResult(mode) {
  $("network-form").hidden = true;
  $("network-result").hidden = false;
  const steps = $("network-result-steps");
  steps.replaceChildren();
  const values = mode === "station"
    ? ["Wait for the radio to restart.", "Reconnect this phone or computer to your home Wi-Fi.", "Read the new DHCP IP on the screen and open it in your browser."]
    : ["Wait for the radio to restart in setup AP mode.", "Reconnect this phone or computer to the Wi-Fi shown on the screen.", "Open 192.168.4.1 in your browser."];
  for (const value of values) steps.append(element("li", "", value));
  text("network-result-title", mode === "station" ? "Reconnect to your home Wi-Fi" : "Reconnect to the setup AP");
  const note = $("network-result-note");
  note.replaceChildren();
  if (mode === "station") {
    const user = state.network?.loginUser || "meshcore";
    const key = state.network?.loginKey || "the 8-letter device key";
    note.append("When prompted, use ", element("strong", "", `${user} / ${key}`), ". This is the device key, not your home Wi-Fi password.");
  } else {
    note.textContent = "The screen and USB serial console show the setup Wi-Fi name and device key after restart.";
  }
}

function renderMore() {
  const self = state.self;
  const device = state.device;
  const model = String(device?.manufacturerModel || "").split("\0", 1)[0]
    .replace(/[^\x20-\x7e]/g, "").replace(/_/g, " ").trim();
  text("more-name", self?.name || "RADIO");
  text("more-model", model || "MeshCore Companion");
  text("more-firmware", device?.firmwareVer != null ? `Protocol ${device.firmwareVer}` : "—");
  text("more-build", device?.firmware_build_date || "—");
  text("more-key", self?.publicKey ? `${hex(self.publicKey, 8)}…` : "—");
  text("app-version", __APP_VERSION__);
  text("library-version", __MESHCORE_JS_VERSION__);
  $("advert-button").disabled = !state.connected;
  $("clear-history-button").disabled = !state.messages.length;
}

function drawUltimateChart(canvas, rows, keys, colors) {
  if (!canvas) return;
  const ratio = devicePixelRatio || 1;
  const width = Math.max(canvas.clientWidth, 280);
  const height = Math.max(canvas.clientHeight, 160);
  canvas.width = width * ratio; canvas.height = height * ratio;
  const ctx = canvas.getContext("2d");
  ctx.scale(ratio, ratio); ctx.clearRect(0, 0, width, height);
  ctx.strokeStyle = "rgba(255,255,255,.055)"; ctx.lineWidth = 1;
  for (let y = 18; y < height; y += 35) { ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(width, y); ctx.stroke(); }
  const ordered = [...(rows || [])].reverse();
  if (ordered.length < 2) return;
  const trafficKeys = keys.filter((key) => key !== "battery");
  const trafficMaximum = Math.max(1,
    ...ordered.flatMap((row) => trafficKeys.map((key) => Number(row[key]) || 0)));
  keys.forEach((key, series) => {
    ctx.strokeStyle = colors[series]; ctx.lineWidth = series ? 1.6 : 2.4;
    ctx.shadowColor = colors[series]; ctx.shadowBlur = series ? 4 : 9; ctx.beginPath();
    let drawing = false;
    ordered.forEach((row, index) => {
      const x = index * width / (ordered.length - 1);
      const raw = Number(row[key]);
      if (!Number.isFinite(raw) || (key === "battery" && raw <= 0)) {
        drawing = false;
        return;
      }
      const scaled = key === "battery" ? Math.max(0, Math.min(1, (raw - 3000) / 1200))
                                        : raw / trafficMaximum;
      const y = height - 10 - scaled * (height - 26);
      drawing ? ctx.lineTo(x, y) : ctx.moveTo(x, y);
      drawing = true;
    });
    ctx.stroke(); ctx.shadowBlur = 0;
  });
}

function renderUltimate() {
  const ultimate = state.ultimate;
  if (!ultimate) return;
  text("ultimate-gate", ultimate.memoryGate ? "PASS" : "PENDING");
  $("ultimate-gate-dot").className = ultimate.memoryGate ? "pass" : "";
  text("ultimate-history-count", formatNumber(ultimate.historyCount || 0));
  text("ultimate-history-capacity", `${formatNumber(ultimate.historyCapacity || 0)} record capacity`);
  text("ultimate-free-heap", `${Math.round((ultimate.freeHeap || 0) / 1024)} KB`);
  text("ultimate-largest", `largest ${Math.round((ultimate.largestAllocation || 0) / 1024)} KB`);
  text("ultimate-flush", `${((ultimate.displayFlushUs || 0) / 1000).toFixed(1)} ms`);
  text("ultimate-tiles", `${ultimate.displayTiles || 0} / 176 tiles`);
  text("ultimate-drops", ultimate.eventDrops || 0);
  text("ultimate-queue", `outbound ${ultimate.queueDepth || 0} · air ${Math.round((ultimate.airtimeMs || 0) / 60000)}m`);
  const profileNames = ["BALANCED", "FIELD", "BATTERY"];
  const trend = Number(ultimate.batteryTrendMvPerHour || 0);
  text("ultimate-battery", ultimate.batteryMv > 0
    ? `${(ultimate.batteryMv / 1000).toFixed(2)} V · ${trend > 0 ? "+" : ""}${trend} mV/h`
    : "—");
  text("ultimate-runtime", ultimate.usbHostConnected
    ? "USB host connected · unplug to start a clean discharge window"
    : ultimate.batteryRuntimeMinutes > 0
    ? `about ${Math.floor(ultimate.batteryRuntimeMinutes / 60)}h ${ultimate.batteryRuntimeMinutes % 60}m to 3.45 V`
    : `${profileNames[ultimate.powerProfile] || "BALANCED"} · ${ultimate.animationFrameMs || 66} ms frames`);
  const delivery = ultimate.delivery || {};
  text("ultimate-delivery", String(delivery.state || "idle").toUpperCase());
  text("ultimate-delivery-target", delivery.target
    ? `${delivery.target}${delivery.roundTripMs ? ` · ${delivery.roundTripMs} ms` : ""}`
    : "No on-device send");

  const minuteRows = (ultimate.minutes || [])
    .map((row) => ({ rx: row[1], tx: row[2], battery: row[7] }));
  const hourRows = (ultimate.hours || [])
    .map((row) => ({ rx: row[1], tx: row[2], failures: row[3] }));
  drawUltimateChart($("ultimate-minute-chart"), minuteRows,
    ["rx", "tx", "battery"], ["#44f08a", "#39e7ff", "#ffb84d"]);
  drawUltimateChart($("ultimate-week-chart"), hourRows, ["rx", "tx", "failures"], ["#44f08a", "#39e7ff", "#ff5468"]);

  const nodes = ultimate.nodes || [];
  text("ultimate-node-count", `${nodes.length} radio${nodes.length === 1 ? "" : "s"}`);
  const nodeList = $("ultimate-node-list"); nodeList.replaceChildren();
  if (!nodes.length) nodeList.append(element("p", "empty-state", "Listening on the configured preset."));
  nodes.slice(0, 16).forEach((node) => {
    const item = element("div", "ultimate-node");
    item.append(element("strong", "", node.name || "Unnamed radio"),
                element("b", "", Number.isFinite(node.rssi) ? `${node.rssi} dBm` : `${node.path} hops`),
                element("span", "", `Role ${node.role} · ${formatAge(node.seen)}`),
                element("span", "", `${node.packets} packet${node.packets === 1 ? "" : "s"}`));
    nodeList.append(item);
  });

  const historyList = $("ultimate-history-list"); historyList.replaceChildren();
  if (!state.ultimateHistory.length) historyList.append(element("p", "empty-state", "No stored messages."));
  state.ultimateHistory.forEach((record) => {
    const item = element("div", "ultimate-record");
    item.append(element("strong", "", record.sender || (record.kind === 2 ? "#channel" : "Direct")),
                element("span", "", `${record.incoming ? "Received" : "Sent"} · ${formatAge(record.timestamp)}`),
                element("p", "", record.text || ""));
    historyList.append(item);
  });

  const settings = state.ultimateSettings;
  if (settings) {
    $("ultimate-history-cap").value = String(settings.historyCapacity);
    $("ultimate-cadence").value = String(settings.scanCadenceMs);
    $("ultimate-power-profile").value = String(settings.powerProfile || 0);
    $("ultimate-battery-capacity").value = String(settings.batteryCapacityMah || 0);
    $("ultimate-battery-calibration").value = String(settings.batteryCalibrationMv || 0);
    $("ultimate-private").checked = Boolean(settings.privateNotifications);
    const phrases = $("ultimate-phrases");
    if (!phrases.children.length) (settings.quickPhrases || []).forEach((phrase, index) => {
      const label = element("label", "", `Quick phrase ${index + 1}`);
      const input = element("input"); input.type = "text"; input.maxLength = 47;
      input.dataset.phrase = index; input.value = phrase; label.append(input); phrases.append(label);
    });
  }
  renderDashboardAnalytics();
  if (state.view === "radio") requestAnimationFrame(drawRadioActivity);
}

function renderAll() {
  if (state.battery) {
    const volts = `${(state.battery / 1000).toFixed(2)} V`;
    text("battery-chip", volts);
  } else {
    text("battery-chip", "—");
  }
  renderHome(); renderMessages(); renderRadio(); renderNetwork(); renderUltimate(); renderMore(); repeaterTools.render(); contactsTools.render(); monitoring.render(); secureComs.refresh(); contactMap.schedule();
}

function showView(view) {
  state.view = view;
  document.querySelectorAll(".screen").forEach((screen) => screen.classList.toggle("active", screen.dataset.screen === view));
  document.querySelectorAll(".nav button").forEach((button) => button.classList.toggle("active", button.dataset.view === view));
  const labels = {
    situation: ["Situation map", "SHARED MAP"], home: ["Overview", "NEONPOCKETMC"], contacts: ["Contacts", "MESHCORE"], monitoring: ["Monitoring", "NODE HEALTH"], secure: ["Secure Coms", "PRIVATE APP-TO-APP"], messages: ["Messages", "CHANNELS & DIRECT"],
    contactmap: ["Contact map", "CONTACTS & PACKETS"], watch: ["Word alerts", "MESSAGE MONITOR"], repeaters: ["Repeaters", "MESHCORE"], terrain: ["Terrain tools", "LINK PLANNING"], radio: ["Radio", "LIVE RF"], ultimate: ["Settings", "MESHCORE RADIO"],
  };
  text("screen-title", labels[view]?.[0] || "Ultimate");
  text("screen-kicker", labels[view]?.[1] || "NEONPOCKETMC");
  if (view === "contacts") contactsTools.render();
  if (view === "monitoring") monitoring.render();
  if (view === "secure") secureComs.refresh();
  if (view === "situation") situationMap.visible();
  if (view === "contactmap") contactMap.visible();
  if (view === "messages" && state.selected) selectTarget(state.selected);
  if (view === "repeaters") repeaterTools.repeaterVisible();
  if (view === "terrain") repeaterTools.visible();
  if (view === "radio") renderRadio();
  if (view === "ultimate") { fillMeshSettings(); refreshUltimate(); }
  window.scrollTo({ top: 0, behavior: "smooth" });
}

function closeMessageMenu() {
  $("message-menu").hidden = true;
  messageMenuTarget = null;
}
function quoteExcerpt(value) {
  const valueText = String(value).replace(/\s+/g, " ").trim().replace(/["\\]/g, "'");
  let result = "";
  for (const character of valueText) {
    if (encoder.encode(result + character).length > 45) return result.trimEnd() + "…";
    result += character;
  }
  return result;
}
function openMessageMenu(event, message) {
  event.preventDefault();
  if (!message || message.key !== state.selected) return;
  const selection = window.getSelection();
  const selected = selection && !selection.isCollapsed && event.currentTarget.contains(selection.anchorNode) && event.currentTarget.contains(selection.focusNode) ? selection.toString() : '';
  messageMenuTarget = { key: message.key, text: message.text, copyText: selected || message.text };
  const canSend = Boolean(state.connected && targetInfo(message.key));
  $("message-menu-reply").disabled = !canSend; $("message-menu-like").disabled = !canSend;
  const menu = $("message-menu");
  menu.hidden = false;
  menu.style.left = `${Math.max(4, Math.min(event.clientX, innerWidth - menu.offsetWidth - 4))}px`;
  menu.style.top = `${Math.max(4, Math.min(event.clientY, innerHeight - menu.offsetHeight - 4))}px`;
  $("message-menu-reply").focus();
}
$("message-menu-copy").addEventListener("click", async () => {
  const target = messageMenuTarget; closeMessageMenu(); if (!target) return;
  try { await navigator.clipboard.writeText(target.copyText); toast("Text copied"); }
  catch { toast("Could not copy text. Select it and press Ctrl+C.", "error"); }
});
$("message-menu-reply").addEventListener("click", () => {
  const target = messageMenuTarget; closeMessageMenu();
  if (!target || target.key !== state.selected || !state.connected) return;
  const input = $("message-input");
  input.value = `↪ "${quoteExcerpt(target.text)}"\n${input.value}`;
  updateComposeCount(); input.focus(); input.setSelectionRange(input.value.length, input.value.length);
});
$("message-menu-like").addEventListener("click", () => {
  const target = messageMenuTarget; closeMessageMenu();
  if (!target || target.key !== state.selected || !state.connected) return;
  sendMessage(null, `👍 Re: "${quoteExcerpt(target.text)}"`);
});
document.addEventListener("pointerdown", (event) => { if (!$("message-menu").contains(event.target)) closeMessageMenu(); });
document.addEventListener("keydown", (event) => { if (event.key === "Escape") closeMessageMenu(); });
window.addEventListener("resize", closeMessageMenu);

async function sendMessage(event, quickText = null) {
  event?.preventDefault();
  const input = $("message-input");
  const messageText = (quickText === null ? input.value : quickText).trim();
  const info = targetInfo(state.selected);
  if (!messageText || !info) return;
  if (encoder.encode(messageText).length > MAX_MESSAGE_BYTES) return toast("Message is over 140 bytes", "error");
  const region = $("region-select").value === "custom" ? $("custom-region").value.trim().replace(/^#/, "") : $("region-select").value;
  if (region !== "default" && (!region || /[\s\x00-\x1f]/.test(region) || encoder.encode(region).length > 30)) return toast("Enter a valid region name of up to 30 bytes", "error");
  const outgoing = addMessage({ key: state.selected, text: messageText, timestamp: Math.floor(Date.now() / 1000), direction: "out", status: "sending", unread: false });
  if (quickText === null) { input.value = ""; updateComposeCount(); }
  const sendStarted = Date.now();
  try {
    const response = await exclusive(async () => {
      const override = region !== "default";
      if (override) { const hash = await crypto.subtle.digest("SHA-256", encoder.encode("#" + region)); await connection.setFloodScope(new Uint8Array(hash.slice(0,16))); }
      try {
        return await (info.channel ? connection.sendChannelTextMessage(info.channel.channelIdx, messageText) : connection.sendTextMessage(info.contact.publicKey, messageText));
      } finally {
        if (override) await connection.clearFloodScope();
      }
    });
    outgoing.status = "queued";
    if (info.channel || response?.result === 1) { outgoing.routeType = "flood"; outgoing.floodRegion = region === "default" ? "radio default" : region; }
    else if (response?.result === 0) outgoing.routeType = "direct";
    if (!info.channel && Number.isInteger(response?.expectedAckCrc)) {
      outgoing.expectedAckCrc = response.expectedAckCrc;
      const early = recentAcks.findIndex((ack) => ack.code === outgoing.expectedAckCrc && ack.time >= sendStarted);
      if (early >= 0) { recentAcks.splice(early, 1); outgoing.status = "delivered"; }
    }
    persistHistory();
    toast("Message queued");
  } catch {
    outgoing.status = "failed";
    persistHistory();
    toast("Message failed", "error");
  }
  renderMessages();
}


function fillMeshSettings() {
  const self = state.self;
  if (!self || $("mesh-settings").contains(document.activeElement)) return;
  $("setting-name").value = self.name || "";
  $("setting-freq").value = self.radioFreq / 1000;
  $("setting-bw").value = String(self.radioBw / 1000);
  $("setting-sf").value = String(self.radioSf);
  $("setting-cr").value = String(self.radioCr);
  $("setting-power").value = self.txPower;
  $("setting-power").max = Math.min(22, self.maxTxPower);
  $("setting-contacts").value = String(self.manualAddContacts ? 1 : 0);
  $("setting-lat").value = self.advLat / 1000000;
  $("setting-lon").value = self.advLon / 1000000;
  text("setting-channel-list", state.channels.length ? "Configured slots: " + state.channels.map(c => `${c.channelIdx}: ${c.name}`).join(", ") : "No channels configured.");
}
// Protocol v10+ appends the companion's path mode to DEVICE_INFO. The
// library version in use does not decode this field, so read the frame.
function pathHashCommand(radio, bytes, responseCode) {
  return new Promise((resolve,reject)=>{
    let timer,done=false;
    const finish=(error,value)=>{if(done)return;done=true;clearTimeout(timer);radio.off('rx',onFrame);error?reject(error):resolve(value)};
    const onFrame=frame=>{if(frame[0]===Constants.ResponseCodes.Err)return finish(Error('Radio rejected the path-size request.'));if(frame[0]!==responseCode)return;if(responseCode===Constants.ResponseCodes.DeviceInfo){if(frame.length<82||frame[1]<10||frame[81]>2)return finish(Error('This radio does not report a supported path-size setting.'));return finish(null,frame[81])}finish(null)};
    radio.on('rx',onFrame);timer=setTimeout(()=>finish(Error('Radio did not respond.')),8000);radio.sendToRadioFrame(bytes).catch(finish);
  });
}
async function readPathHash(radio) { return pathHashCommand(radio,new Uint8Array([22,Constants.SupportedCompanionProtocolVersion]),Constants.ResponseCodes.DeviceInfo); }
function displayPathHash(mode) { $('setting-path-hash').value=String(mode);text('path-hash-status',`Current radio setting: ${mode+1} byte${mode?'s':''} per hop.`); }
$('read-path-hash').onclick=async()=>{if(!state.connected){text('path-hash-status','Connect the radio first.');return}const button=$('read-path-hash'),radio=connection;button.disabled=true;text('path-hash-status','Reading current setting…');try{const mode=await exclusive(()=>readPathHash(radio));if(connection!==radio||!state.connected)throw Error('Radio connection changed.');displayPathHash(mode)}catch(e){text('path-hash-status',e?.message||'Could not read path size.')}finally{button.disabled=false}};
$('path-hash-form').onsubmit=async event=>{event.preventDefault();const chosen=$('setting-path-hash').value,mode=Number(chosen);if(!state.connected){text('path-hash-status','Connect the radio first.');return}if(chosen===''||![0,1,2].includes(mode)){text('path-hash-status','Choose a path size.');return}const radio=connection,button=event.currentTarget.querySelector('button[type=submit]');button.disabled=true;text('path-hash-status','Saving and checking…');try{const actual=await exclusive(async()=>{await pathHashCommand(radio,new Uint8Array([61,0,mode]),Constants.ResponseCodes.Ok);return readPathHash(radio)});if(connection!==radio||!state.connected)throw Error('Radio connection changed.');if(actual!==mode)throw Error(`Radio reports ${actual+1} byte(s); the requested setting was not confirmed.`);displayPathHash(actual);toast('Path size saved and verified.')}catch(e){text('path-hash-status',e?.message||'Could not verify path size.')}finally{button.disabled=false}};
function settingForm(id, action) {
  $(id).addEventListener("submit", async event => {
    event.preventDefault();
    if (!state.connected) return toast("Connect to the radio first", "error");
    const button = event.currentTarget.querySelector('button[type="submit"], button:not([type])');
    if (button) button.disabled = true;
    try {
      await exclusive(async () => { await action(); state.self = await connection.getSelfInfo(6000); });
      fillMeshSettings(); renderHome(); renderRadio(); renderMore();
      toast("Setting saved to the radio");
    } catch (error) { toast(error?.message || "Could not save setting", "error"); }
    finally { if (button) button.disabled = false; }
  });
}
settingForm("node-name-form", async () => {
  const name = $("setting-name").value.trim();
  if (!name || encoder.encode(name).length > 31) throw new Error("Name must be 1–31 UTF-8 bytes");
  await connection.setAdvertName(name);
});
$("uk-preset-button").addEventListener("click", () => {
  $("setting-freq").value = "869.618"; $("setting-bw").value = "62.5"; $("setting-sf").value = "8"; $("setting-cr").value = "8";
});
settingForm("radio-settings-form", () => connection.setRadioParams(
  Math.round(Number($("setting-freq").value) * 1000), Math.round(Number($("setting-bw").value) * 1000), Number($("setting-sf").value), Number($("setting-cr").value)));
settingForm("power-settings-form", () => connection.setTxPower(Number($("setting-power").value)));
settingForm("contacts-settings-form", () => connection.setOtherParams(Number($("setting-contacts").value) === 1));
settingForm("location-settings-form", () => connection.setAdvertLatLong(Math.round(Number($("setting-lat").value) * 1000000), Math.round(Number($("setting-lon").value) * 1000000)));
settingForm("channel-settings-form", async () => {
  const name = $("setting-channel-name").value.trim();
  if (!name || encoder.encode(name).length > 31) throw new Error("Channel name must be 1–31 UTF-8 bytes");
  const key = $("setting-channel-key").value.trim();
  if (!/^[0-9a-fA-F]{32}$/.test(key)) throw new Error("Channel key must contain 32 hexadecimal characters");
  const secret = new Uint8Array(key.match(/../g).map(v => parseInt(v,16)));
  const slot = Number($("setting-channel-slot").value);
  if (state.channels.some(c => c.channelIdx === slot) && !confirm(`Replace channel in slot ${slot}?`)) throw new Error("Channel change cancelled");
  await connection.setChannel(slot, name, secret);
  state.channels = (await connection.getChannels()).filter(c => c.name);
  normalizeTargets(); renderMessages();
});

function channelTypeChanged() {
  const type = $("channel-add-type").value;
  $("channel-private-fields").hidden = type !== "private";
  $("channel-add-name").disabled = type === "public";
  $("channel-add-help").textContent = type === "hashtag" ? "Hashtag keys are derived from the exact channel name. Anyone who knows it can read the channel." : type === "private" ? "Share the name and key securely with other members. Copy a generated key before leaving this page." : "The standard MeshCore public channel is readable by anyone.";
}
$("channel-add-type").addEventListener("change", channelTypeChanged);
$("channel-generate").addEventListener("click", () => { const secret = new Uint8Array(16); crypto.getRandomValues(secret); $("channel-add-key").value = hex(secret); $("channel-add-status").textContent = "Key generated. Copy it and share it securely with members."; });
function parseChannelSecret(value) {
  const input = value.trim(); let bytes;
  if (/^[0-9a-f]{32}$/i.test(input)) bytes = new Uint8Array(input.match(/../g).map(v => parseInt(v,16)));
  else { try { const decoded = atob(input); bytes = Uint8Array.from(decoded, c => c.charCodeAt(0)); } catch { throw Error("Enter a 16-byte key as 32 hexadecimal characters or base64."); } }
  if (bytes.length !== 16 || bytes.every(b => b === 0)) throw Error("Enter a non-zero 16-byte channel key.");
  return bytes;
}
$("channel-add-form").addEventListener("submit", async event => {
  event.preventDefault(); const status = $("channel-add-status"), button = event.currentTarget.querySelector('button[type="submit"]');
  if (!state.connected) { status.textContent = "Connect the radio first."; return; }
  button.disabled = true; status.textContent = "Checking radio channel slots…";
  try {
    const type = $("channel-add-type").value; let name = $("channel-add-name").value.trim(), secret;
    if (type === "public") { name = "Public"; secret = parseChannelSecret("8b3387e9c5cdea6ac9e5edbaa115cd72"); }
    else if (type === "hashtag") { if (!name.startsWith("#")) name = "#" + name; if (name === "#") throw Error("Enter a hashtag channel name."); secret = new Uint8Array((await crypto.subtle.digest("SHA-256", encoder.encode(name))).slice(0,16)); }
    else { if (!name || name.startsWith("#")) throw Error("Enter a private channel name without #."); secret = parseChannelSecret($("channel-add-key").value); }
    if (encoder.encode(name).length > 31 || /[\x00-\x1f]/.test(name)) throw Error("Channel name must be 1–31 UTF-8 bytes and contain no control characters.");
    const result = await exclusive(async () => {
      const slots = await connection.getChannels();
      if (!slots.length) throw Error("Could not read channel slots from the radio.");
      if (slots.some(c => c.name === name)) throw Error("This channel name is already on the radio.");
      const empty = slots.find(c => !c.name && c.secret?.length === 16 && c.secret.every(byte => byte === 0));
      if (!empty) throw Error("No empty channel slot is available on the radio.");
      await connection.setChannel(empty.channelIdx, name, secret);
      const saved = await connection.getChannel(empty.channelIdx);
      if (saved.name !== name || saved.secret?.length !== 16 || !saved.secret.every((b,i) => b === secret[i])) throw Error("The radio did not confirm the saved channel. Refresh before retrying.");
      return { slot: empty.channelIdx, channels: slots.map(c => c.channelIdx === empty.channelIdx ? saved : c).filter(c => c.name) };
    });
    state.channels = result.channels; selectTarget(`ch:${result.slot}`); fillMeshSettings();
    status.textContent = `Added ${name} in slot ${result.slot}.` + (type === "private" ? " Keep a copy of the key for other members." : "");
  } catch (error) { status.textContent = error?.message || "Could not add channel."; }
  finally { button.disabled = false; }
});

function updateComposeCount() {
  const count = encoder.encode($("message-input").value).length;
  text("compose-count", `${count} / ${MAX_MESSAGE_BYTES} bytes`);
  $("compose-count").className = count > MAX_MESSAGE_BYTES ? "error" : "";
}

function bindConnectionEvents(radio) {
  radio.on(Constants.ResponseCodes.ContactMsgRecv, (message) => { unannouncedMessages += ingestWaiting({ contactMessage: message }); });
  radio.on(Constants.ResponseCodes.ChannelMsgRecv, (message) => { unannouncedMessages += ingestWaiting({ channelMessage: message }); });
  radio.on("connected", () => {
    if (!radio.ready || !radio.ownsLease()) return;
    setLink("connected");
    toast("Radio connected");
    setTimeout(() => $("boot").classList.add("hidden"), 220);
    exclusive(syncAll).then(() => { recoveryAttempts = 0; }).catch(() => toast("Initial sync failed", "error"));
  });
  radio.on("disconnected", () => setLink("offline"));
  radio.on("transport-busy", () => {
    setLink("busy");
    $("boot").classList.add("hidden");
    toast("Another tab controls this radio. Close it, then tap the link status to retry.", "warn");
  });
  radio.on("transport-state", () => { setLink("reconnecting"); toast("Link interrupted — reconnecting", "warn"); });
  radio.on("transport-restored", () => {
    setLink("connected");
    toast("Link restored — resyncing");
    exclusive(syncAll).then(() => { recoveryAttempts = 0; }).catch(() => toast("Resync failed", "error"));
  });
  radio.on(Constants.PushCodes.MsgWaiting, () => { if (radio.ready) exclusive(syncMessages).catch(() => toast("Message sync failed", "error")); });
  radio.on(Constants.PushCodes.NewAdvert, (advert) => {
    const existing = state.contacts.findIndex((item) => hex(item.publicKey) === hex(advert.publicKey));
    if (existing >= 0) state.contacts[existing] = advert; else state.contacts.unshift(advert);
    refreshNearbyFromContacts(); renderAll(); toast(`${advert.advName || "A node"} is nearby`, "warn");
  });
  radio.on(Constants.PushCodes.Advert, () => { if (radio.ready) exclusive(refreshContacts).catch(() => {}); });
  radio.on(Constants.PushCodes.SendConfirmed, (confirmation) => {
    const pending = [...state.messages].reverse().find((item) => item.direction === "out" && (item.key.startsWith("dm:") || item.key.startsWith("room:")) && item.status === "queued" && Number.isInteger(item.expectedAckCrc) && item.expectedAckCrc === confirmation.ackCode);
    if (pending) { pending.status = "delivered"; persistHistory(); renderMessages(); toast(pending.key.startsWith("room:") ? "Delivered to room server" : "Direct message delivered"); }
    else if (Number.isInteger(confirmation.ackCode)) { recentAcks.push({code: confirmation.ackCode, time: Date.now()}); while (recentAcks.length > 40 || recentAcks[0]?.time < Date.now() - 60000) recentAcks.shift(); }
  });
  radio.on(Constants.PushCodes.LogRxData, (sample) => {
    contactMap.ingest(sample);
    state.radio = { ...(state.radio || {}), lastRssi: sample.lastRssi, lastSnr: sample.lastSnr };
    state.rfSamples.push(sample.lastRssi); state.rfSamples = state.rfSamples.slice(-36); renderHome(); renderRadio();
  });
}

document.querySelectorAll(".nav button").forEach((button) => button.addEventListener("click", () => showView(button.dataset.view)));
$("message-search").addEventListener("input", renderMessages);
function readDefaultScope(radio) { return new Promise((resolve,reject)=>{ let timer; const finish=(error,value)=>{clearTimeout(timer);radio.off('rx',onFrame);error?reject(error):resolve(value)}; const onFrame=frame=>{if(frame[0]===28){if(frame.length===1)return finish(null,'');if(frame.length<48)return finish(Error('Incomplete scope reply'));const bytes=frame.slice(1,32),end=bytes.indexOf(0);return finish(null,new TextDecoder().decode(end<0?bytes:bytes.slice(0,end)))}if(frame[0]===Constants.ResponseCodes.Err)finish(Error('Radio does not support default scope'))};radio.on('rx',onFrame);timer=setTimeout(()=>finish(Error('No response from radio')),5000);radio.sendToRadioFrame(new Uint8Array([64])).catch(finish); }); }
async function checkScotSetup(){ if(!state.connected)throw Error('Connect the radio first.');const radio=connection,self=state.self;const scope=await exclusive(()=>readDefaultScope(radio));if(connection!==radio||!state.connected)throw Error('Radio connection changed.');const match=self && self.radioFreq===869618 && self.radioBw===62500 && self.radioSf===8 && self.radioCr===8;const preset=self ? (match?'UK Narrow matches':'Radio settings differ from UK Narrow; check Settings'):'Radio settings unavailable';$('radio-default-region').textContent=scope?`Radio default: ${scope}`:'Radio default: unscoped';$('scot-check-result').textContent=`${preset} · Default scope: ${scope||'unscoped'}${scope==='sco'?' (ScotMesh recommended)':' · ScotMesh recommends sco'}`;return scope; }
$('read-default-region').onclick=async()=>{try{await checkScotSetup()}catch(e){$('radio-default-region').textContent='Radio default unavailable: '+(e?.message||'request failed')}};
$('scot-check').onclick=async()=>{ $('scot-check-result').textContent='Checking…';try{await checkScotSetup()}catch(e){$('scot-check-result').textContent=e?.message||'Check failed';} };
$('scot-set-default').onclick=async()=>{const button=$('scot-set-default');if(!state.connected){$('scot-check-result').textContent='Connect the radio first.';return}button.disabled=true;try{const radio=connection,hash=new Uint8Array(await crypto.subtle.digest('SHA-256',encoder.encode('#sco'))),frame=new Uint8Array(48);frame[0]=63;frame.set(encoder.encode('sco'),1);frame.set(hash.slice(0,16),32);await exclusive(async()=>{await pathHashCommand(radio,frame,Constants.ResponseCodes.Ok);const actual=await readDefaultScope(radio);if(actual!=='sco')throw Error('Radio did not confirm sco; reported '+(actual||'unscoped'));});if(connection!==radio||!state.connected)throw Error('Radio connection changed.');await checkScotSetup();toast('Radio default scope saved and verified.');}catch(e){$('scot-check-result').textContent=e?.message||'Could not set default scope';}finally{button.disabled=false}};
$('scot-discover').onclick=()=>{showView('contacts');$('contacts-discover').click()};
$('scot-test').onclick=()=>{$('scot-test-help').hidden=false;};
$('region-select').addEventListener('change', () => { $('custom-region').hidden = $('region-select').value !== 'custom'; if ($('region-select').value !== 'custom') { saveChannelScope(state.selected, $('region-select').value); renderMessages(); } });
$('custom-region').addEventListener('change', () => { const value=$('custom-region').value.trim().replace(/^#/, ''); if(value && !/[\s\x00-\x1f]/.test(value) && encoder.encode(value).length<=30) { saveChannelScope(state.selected,value); renderMessages(); } });
$("channel-mute").addEventListener("click", () => toggleChannelMute(state.selected));
$("home-open-unread").addEventListener("click", () => {
  const latest = [...state.messages].reverse().find(item => item.unread && !channelIsMuted(item.key));
  if (latest && targetInfo(latest.key)) selectTarget(latest.key);
  showView("messages");
});
$("home-sync").addEventListener("click", async () => {
  if (!state.connected || syncing) return;
  const button = $("home-sync"); button.disabled = true; text("home-action-status", "Synchronising…");
  try { await exclusive(syncAll); text("home-action-status", "Radio synchronised."); }
  catch { text("home-action-status", "Sync failed. Check the connection."); }
  finally { button.disabled = !state.connected; }
});
$("home-local-advert").addEventListener("click", () => sendRadioAdvert("local"));
$("home-flood-advert").addEventListener("click", () => sendRadioAdvert("flood"));
$("messages-direct").onclick = () => { messageTab = "direct"; renderMessages(); };
$("messages-channels").onclick = () => { messageTab = "channels"; renderMessages(); };
$("messages-rooms").onclick = () => { messageTab = "rooms"; renderMessages(); };
document.querySelectorAll("[data-open]").forEach((button) => button.addEventListener("click", () => showView(button.dataset.open)));
$("target-select").addEventListener("change", (event) => selectTarget(event.target.value));
$("composer").addEventListener("submit", sendMessage);
$("message-input").addEventListener("input", updateComposeCount);
$("message-input").addEventListener("keydown", (event) => {
  if (event.key !== "Enter" || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey || event.isComposing || event.keyCode === 229) return;
  event.preventDefault();
  const send = $("composer").querySelector("button[type=submit]");
  if (!event.repeat && !send.disabled) $("composer").requestSubmit(send);
});
$("refresh-button").addEventListener("click", () => exclusive(syncAll).then(() => toast("Sync complete")).catch(() => toast("Sync failed", "error")));
function updateAdvertControls() {
  for (const id of ["radio-advert-local", "radio-advert-flood", "home-local-advert", "home-flood-advert"]) $(id).disabled = !state.connected || advertSending;
  if (!state.connected && !advertSending) $("radio-advert-status").textContent = "Connect to your radio to send an advert.";
}
async function sendRadioAdvert(kind) {
  if (!state.connected || advertSending) return;
  advertSending = true; updateAdvertControls();
  const label = kind === "local" ? "Local" : "Flood";
  $("radio-advert-status").textContent = `Sending ${kind} advert…`;
  try {
    await exclusive(() => kind === "local" ? connection.sendZeroHopAdvert() : connection.sendFloodAdvert());
    $("radio-advert-status").textContent = `${label} advert queued on the radio.`;
    toast(`${label} advert queued`);
  } catch {
    $("radio-advert-status").textContent = `${label} advert failed. Check the radio connection and try again.`;
    toast(`${label} advert failed`, "error");
  } finally { advertSending = false; updateAdvertControls(); }
}
$("radio-advert-local").addEventListener("click", () => sendRadioAdvert("local"));
$("radio-advert-flood").addEventListener("click", () => sendRadioAdvert("flood"));
$("advert-button").addEventListener("click", async () => {
  $("advert-button").disabled = true;
  try { await exclusive(() => connection.sendFloodAdvert()); toast("Advert queued", "warn"); }
  catch { toast("Advert failed", "error"); }
  finally { $("advert-button").disabled = false; }
});
$("connect-button").addEventListener("click", () => {
  recoveryAttempts = 0;
  if (state.connected) return exclusive(syncAll).catch(() => toast("Sync failed", "error"));
  return connection.connect().catch(() => toast("Could not reconnect", "error"));
});
$("network-setup-button").addEventListener("click", openNetworkDialog);
$("network-dialog-close").addEventListener("click", closeNetworkDialog);
$("network-cancel").addEventListener("click", closeNetworkDialog);
$("network-done").addEventListener("click", closeNetworkDialog);
$("network-reveal").addEventListener("click", () => {
  const password = $("network-password");
  const revealed = password.type === "password";
  password.type = revealed ? "text" : "password";
  $("network-reveal").textContent = revealed ? "Hide" : "Show";
  $("network-reveal").setAttribute("aria-pressed", String(revealed));
});
$("network-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const ssid = $("network-ssid").value;
  const password = $("network-password").value;
  const ssidBytes = encoder.encode(ssid).length;
  const passwordBytes = encoder.encode(password).length;
  if (ssidBytes < 1 || ssidBytes > 32) { toast("Wi-Fi name must be 1–32 bytes", "error"); return $("network-ssid").focus(); }
  if (passwordBytes !== 0 && (passwordBytes < 8 || passwordBytes > 64)) { toast("Password must be empty or 8–64 bytes", "error"); return $("network-password").focus(); }
  $("network-save").disabled = true;
  try {
    const result = await setNetwork({ mode: "station", ssid, password });
    state.network = { ...state.network, loginUser: result.loginUser || "meshcore", loginKey: result.loginKey || state.network?.loginKey || "" };
    renderNetwork();
    showNetworkResult("station");
    toast("Local Wi-Fi saved. Radio is restarting.", "warn");
  } catch { toast("Could not save Wi-Fi settings", "error"); }
  finally { $("network-save").disabled = false; }
});
$("return-ap-button").addEventListener("click", async () => {
  if (!confirm("Return the radio to setup AP mode? The device will restart.")) return;
  $("return-ap-button").disabled = true;
  try {
    await setNetwork({ mode: "ap" });
    openNetworkDialog();
    showNetworkResult("ap");
    toast("Setup AP requested. Radio is restarting.", "warn");
  } catch { toast("Could not return to setup AP", "error"); }
  finally { $("return-ap-button").disabled = false; }
});
$("clear-history-button").addEventListener("click", () => {
  if (!state.messages.length || !confirm("Clear locally stored message history for this node?")) return;
  try { localStorage.removeItem(historyKey()); } catch { /* storage is optional */ }
  state.messages = [];
  renderAll();
  toast("Local message history cleared");
});
$("ultimate-settings-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const quickPhrases = [...document.querySelectorAll("[data-phrase]")].map((input) => input.value);
  try {
    const response = await ultimateFetch("/api/ultimate/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        historyCapacity: Number($("ultimate-history-cap").value),
        scanCadenceMs: Number($("ultimate-cadence").value),
        powerProfile: Number($("ultimate-power-profile").value),
        batteryCapacityMah: Number($("ultimate-battery-capacity").value),
        batteryCalibrationMv: Number($("ultimate-battery-calibration").value),
        privateNotifications: $("ultimate-private").checked,
        quickPhrases,
      }),
    });
    state.ultimateSettings = await response.json();
    renderUltimate(); toast("Device settings saved");
  } catch { toast("Could not save device settings", "error"); }
});
$("ultimate-export").addEventListener("click", () => {
  window.location.assign("/api/ultimate/export");
});
$("ultimate-clear").addEventListener("click", async () => {
  if (!confirm("Permanently erase the /np/ message journal? MeshCore identity and contacts are preserved.")) return;
  try {
    await ultimateFetch("/api/ultimate/history", { method: "DELETE", headers: { "X-NP-Confirm": "clear" } });
    await refreshUltimate(); toast("On-device history cleared", "warn");
  } catch { toast("History clear failed", "error"); }
});

async function saveBrowserLocation(latitude, longitude, accuracy) {
  text("ultimate-location-preview", `${latitude.toFixed(6)}, ${longitude.toFixed(6)} · ±${Math.round(accuracy)} m`);
  const advertise = $("ultimate-location-share").checked;
  const sharing = advertise ? "This location WILL be included in adverts." : "This location will remain private.";
  if (!confirm(`Save this location to MeshCore?\n\n${latitude.toFixed(6)}, ${longitude.toFixed(6)}\nAccuracy ±${Math.round(accuracy)} m\n\n${sharing}`)) return;
  await ultimateFetch("/api/ultimate/location", {
    method: "PUT", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ latitude, longitude, accuracy, advertise }),
  });
  toast(advertise ? "Location saved and enabled for adverts" : "Location saved privately");
}

$("ultimate-location").addEventListener("click", () => {
  const manual = async () => {
    const value = prompt("Browser location is unavailable on this connection. Enter latitude,longitude:", "");
    if (!value) return;
    const [latitude, longitude] = value.split(",").map(Number);
    if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) return toast("Enter latitude,longitude", "error");
    try { await saveBrowserLocation(latitude, longitude, 0); } catch { toast("Location save failed", "error"); }
  };
  if (!navigator.geolocation) return manual();
  navigator.geolocation.getCurrentPosition(
    (position) => saveBrowserLocation(position.coords.latitude, position.coords.longitude,
                                      position.coords.accuracy).catch(() => toast("Location save failed", "error")),
    manual, { enableHighAccuracy: true, timeout: 12000, maximumAge: 0 });
});

$("ultimate-ota-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const file = $("ultimate-ota-file").files[0];
  if (!file || !file.name.toLowerCase().endsWith(".npu")) return toast("Choose a NeonPocket .npu package", "error");
  if (!confirm(`Verify and install ${file.name}? The radio will restart only after board, mode, hash, and signature checks pass.`)) return;
  const body = new FormData(); body.append("package", file, file.name);
  const button = $("ultimate-ota-form").querySelector("button"); button.disabled = true;
  try {
    const response = await fetch("/api/ultimate/ota", { method: "POST", body, credentials: "same-origin" });
    const result = await response.json().catch(() => ({}));
    if (!response.ok || result.result !== "success") throw new Error(result.result || "upload-failed");
    toast("Signature verified. Restarting into the new app.", "warn");
  } catch (error) { toast(`Update rejected: ${error.message}`, "error"); button.disabled = false; }
});
window.addEventListener("resize", () => {
  if (state.view === "home") renderDashboardAnalytics();
  if (state.view === "radio") { drawSignalChart(); drawRadioActivity(); }
  if (state.view === "ultimate") renderUltimate();
});
document.addEventListener("visibilitychange", () => {
  if (!document.hidden && state.connected) exclusive(async () => {
    await syncMessages();
    await refreshStats();
    await refreshNetworkStatus();
  }).catch(() => toast("Foreground sync failed", "error"));
});
window.addEventListener("pagehide", () => connection.leavePage());
window.addEventListener("pageshow", (event) => {
  if (event.persisted && !connection.running) {
    setLink("reconnecting");
    connection.connect().catch(() => toast("Could not reconnect", "error"));
  }
});

bindConnectionEvents(connection);
renderAll();
setInterval(() => {
  text("hero-uptime", formatSession());
  if (!document.hidden && state.connected) exclusive(refreshStats).catch(() => {});
  if (!document.hidden) refreshUltimate();
}, 30000);
connection.connect().catch(() => { setLink("offline"); $("boot").classList.add("hidden"); toast("Could not open companion link", "error"); });

document.addEventListener("keydown", event => { if (event.key === "F11" && window.pywebview?.api?.toggle_fullscreen) { event.preventDefault(); window.pywebview.api.toggle_fullscreen(); } });

function initWindowControls(){const api=window.pywebview?.api;if(!api)return;document.getElementById('window-controls').hidden=false;document.getElementById('window-minimize').onclick=()=>api.minimize();document.getElementById('window-restore').onclick=()=>api.toggle_fullscreen();document.getElementById('window-exit').onclick=()=>api.exit_app();}window.addEventListener('pywebviewready',initWindowControls);if(window.pywebview?.api)initWindowControls();


function createRoomTools() {
  const interval = 300000, passwords = new Map(), due = new Map();
  let node = "", rooms = [], busy = false;
  const roomKey = contact => hex(contact.publicKey);
  function load() {
    const current = state.self?.publicKey ? hex(state.self.publicKey) : "";
    if (current === node) return;
    node = current; rooms = []; due.clear(); passwords.clear();
    if (!node) return;
    try { const saved = JSON.parse(localStorage.getItem(`meshcore-rooms-v1:${node}`) || "[]"); if (Array.isArray(saved)) rooms = saved.filter(key => typeof key === "string" && /^[0-9a-f]{64}$/.test(key)); } catch { /* start empty */ }
  }
  function save() { localStorage.setItem(`meshcore-rooms-v1:${node}`, JSON.stringify(rooms)); }
  async function credential(action, key, password) {
    const response = await fetch("/api/repeater-credential", {method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({action,key,password})});
    if (!response.ok) throw Error("Windows Credential Manager is unavailable.");
    return response.json();
  }
  function contacts() { return state.contacts.filter(contact => contact.type === 3); }
  function targets() { load(); return contacts().filter(contact => rooms.includes(roomKey(contact))).map(contact => ({key:`room:${roomKey(contact)}`, name:contact.advName || "Unnamed room", subtitle:"Room server", avatar:"R"})); }
  function render(active) {
    load(); $("room-add-panel").hidden = !active; $("room-actions").hidden = !active;
    const select = $("room-select"), previous = select.value;
    select.replaceChildren(new Option("Choose a known room server", ""));
    for (const contact of contacts()) select.append(new Option(`${contact.advName || "Unnamed room"} · ${roomKey(contact).slice(0,8)}${rooms.includes(roomKey(contact)) ? " (added)" : ""}`, roomKey(contact)));
    select.value = previous && [...select.options].some(option => option.value === previous) ? previous : "";
    $("room-refresh").disabled = busy || !state.selected.startsWith("room:") || !state.connected;
    $("room-history").disabled = busy || !state.selected.startsWith("room:") || !state.connected;
    $("room-remove").disabled = !state.selected.startsWith("room:") || !rooms.includes(state.selected.slice(5));
  }
  async function check(contact, password) {
    if (!state.connected) throw Error("Connect to your radio first.");
    return exclusive(async () => { await syncRadioClock(); return connection.login(contact.publicKey, password, 10000); });
  }
  async function poll(key, manual = false) {
    load(); const contact = contacts().find(item => roomKey(item) === key);
    if (!contact || !rooms.includes(key) || busy || !state.connected) return;
    busy = true; due.set(key, Date.now() + interval); $("room-poll-status").textContent = `Checking ${contact.advName || "room"}…`;
    let stage = "login";
    try {
      let password = passwords.get(key);
      if (password === undefined) { const saved = await credential("get", key); password = saved.password ?? ""; }
      await check(contact, password);
      stage = "queue"; await exclusive(syncMessages);
      $("room-poll-status").textContent = `Connected to ${contact.advName || "room"}. Retained posts may arrive over the next few minutes.`;
      if (manual) toast("Room connected; waiting for retained posts");
    } catch (error) {
      const reason = error?.message || String(error || "no response");
      $("room-poll-status").textContent = stage === "login" ? `Room login failed (${reason}). Check that the room is reachable and the saved password is correct.` : `Room login succeeded, but checking the radio queue failed (${reason}).`;
      if (manual) toast("Room check failed", "error");
    }
    finally { busy = false; renderMessages(); }
  }
  $("room-add-form").addEventListener("submit", async event => {
    event.preventDefault(); load();
    const key = $("room-select").value, contact = contacts().find(item => roomKey(item) === key), password = $("room-password").value;
    if (!node || !contact || !state.connected) { $("room-status").textContent = "Connect your radio and select a known room server."; return; }
    if (encoder.encode(password).length > 15) { $("room-status").textContent = "Room passwords can be at most 15 UTF-8 bytes."; return; }
    if (busy) return;
    busy = true; $("room-status").textContent = "Logging in…";
    try {
      await check(contact, password);
      if ($("room-save-password").checked && password) await credential("save", key, password);
      else await credential("delete", key);
      passwords.set(key, password);
      if (!rooms.includes(key)) rooms.push(key);
      try { save(); } catch { rooms = rooms.filter(item => item !== key); throw Error("Could not save the room on this PC."); }
      due.set(key, Date.now() + interval); $("room-password").value = "";
      $("room-status").textContent = "Room added. Retained posts may arrive gradually while connected.";
      $("room-add-panel").open = false; selectTarget(`room:${key}`);
      try {
        await exclusive(syncMessages);
        $("room-poll-status").textContent = "Connected. Waiting for retained posts; the room server sends them gradually.";
      } catch {
        $("room-poll-status").textContent = "Room added, but the radio message queue could not be checked. Try Check room now.";
      }
    } catch (error) { $("room-status").textContent = `Could not add room: ${error?.message || String(error || "login failed")}`; }
    finally { busy = false; renderMessages(); }
  });
  $("room-refresh").onclick = () => { if (state.selected.startsWith("room:")) void poll(state.selected.slice(5), true); };
  // A direct KEEP_ALIVE can request the room's retained posts. Its ACK is a
  // hash of the tagged request and this radio's public key; SENT only means
  // the companion queued the packet and is not proof of delivery.
  function acknowledgementPayload(raw) {
    if (!(raw instanceof Uint8Array) || raw.length < 6 || ((raw[0] >> 2) & 15) !== 3) return null;
    const route = raw[0] & 3, offset = route === 0 || route === 3 ? 5 : 1;
    if (raw.length <= offset) return null;
    const packed = raw[offset], size = (packed >> 6) + 1, count = packed & 63, end = offset + 1 + size * count;
    if (size > 3 || size * count > 64 || raw.length < end + 4) return null;
    return raw.slice(end, end + 4);
  }
  async function startRetainedRequest(contact) {
    const radio = connection, self = state.self?.publicKey;
    if (!(self instanceof Uint8Array) || self.length !== 32) throw Error("The radio identity is unavailable.");
    const request = new Uint8Array([0x02, 1, 0, 0, 0]), observed = [];
    let expected = null, ackTimer, settled = false, resolveAck;
    const ack = new Promise(resolve => { resolveAck = resolve; });
    const cleanupAck = () => { clearTimeout(ackTimer); radio.off(Constants.PushCodes.LogRxData, onRaw); };
    const finish = value => { if (settled) return; settled = true; cleanupAck(); resolveAck(value); };
    const matches = value => expected && value && value.every((byte, i) => byte === expected[i]);
    const onRaw = sample => { const payload = acknowledgementPayload(sample?.raw); if (!payload) return; if (matches(payload)) finish(true); else if (!expected && observed.length < 100) observed.push(payload); };
    radio.on(Constants.PushCodes.LogRxData, onRaw);
    let response;
    try {
      response = await new Promise((resolve, reject) => {
        let timer;
        const cleanup = () => { clearTimeout(timer); radio.off(Constants.ResponseCodes.Sent, onSent); radio.off(Constants.ResponseCodes.Err, onErr); };
        const onSent = result => { cleanup(); resolve(result); };
        const onErr = () => { cleanup(); reject(Error("The radio could not queue the history request.")); };
        radio.once(Constants.ResponseCodes.Sent, onSent); radio.once(Constants.ResponseCodes.Err, onErr);
        timer = setTimeout(() => { cleanup(); reject(Error("The radio did not respond to the history command.")); }, 10000);
        Promise.resolve().then(() => radio.sendCommandSendBinaryReq(contact.publicKey, request)).catch(error => { cleanup(); reject(error); });
      });
      if (response?.result !== 0) throw Error("The history request used flooding. A known direct route is required; reconnect and try again.");
      if (!Number.isInteger(response.expectedAckCrc)) throw Error("The radio did not provide a history-request tag.");
      const data = new Uint8Array(9 + self.length); new DataView(data.buffer).setUint32(0, response.expectedAckCrc >>> 0, true); data.set(request, 4); data.set(self, 9);
      expected = new Uint8Array(await crypto.subtle.digest("SHA-256", data)).slice(0, 4);
      if (observed.some(matches)) finish(true);
      if (!settled) ackTimer = setTimeout(() => finish(false), Math.max(12000, Math.min(60000, (Number(response.estTimeout) || 0) + 8000)));
      return {ack};
    } catch (error) { finish(false); throw error; }
  }
  async function loadRetainedHistory(key) {
    load(); const contact = contacts().find(item => roomKey(item) === key);
    if (!contact || !rooms.includes(key) || busy || !state.connected) return;
    const radioAtStart = connection, nodeAtStart = node;
    busy = true; due.set(key, Date.now() + interval); renderMessages();
    $("room-poll-status").textContent = `Connecting to ${contact.advName || "room"} to request retained history…`;
    try {
      let password = passwords.get(key);
      if (password === undefined) { const saved = await credential("get", key); password = saved.password ?? ""; }
      let loginFailed = false;
      try { await check(contact, password); } catch { loginFailed = true; }
      if (!state.connected || connection !== radioAtStart || node !== nodeAtStart) throw Error("Radio connection changed; try again.");
      if (loginFailed && state.selected === `room:${key}`) $("room-poll-status").textContent = "Login did not respond; trying the saved room session…";
      const request = await exclusive(() => startRetainedRequest(contact));
      if (state.selected === `room:${key}`) $("room-poll-status").textContent = "History request sent; waiting for the room to acknowledge it…";
      const acknowledged = await request.ack;
      if (!acknowledged) throw Error(loginFailed ? "The room did not acknowledge the request. Check that the room is online, the saved route works and the room password is correct." : "The room did not acknowledge the history request. It may be unreachable or not support replay.");
      await exclusive(syncMessages);
      if (state.selected === `room:${key}`) $("room-poll-status").textContent = "Room acknowledged the history request. Retained posts can arrive gradually; discarded posts and your own posts cannot be replayed.";
    } catch (error) {
      if (state.selected === `room:${key}`) $("room-poll-status").textContent = `Could not request history: ${error?.message || String(error || "login failed")}`;
    } finally { busy = false; renderMessages(); }
  }
  $("room-history").onclick = () => { if (state.selected.startsWith("room:")) void loadRetainedHistory(state.selected.slice(5)); };

  $("room-remove").onclick = async () => {
    load(); const key = state.selected.slice(5); if (!rooms.includes(key)) return;
    try { await credential("delete", key); rooms = rooms.filter(item => item !== key); save(); passwords.delete(key); due.delete(key); state.selected = ""; $("room-poll-status").textContent = "Room removed."; renderMessages(); }
    catch { toast("Could not remove the room password", "error"); }
  };
  setInterval(() => {
    load(); if (!state.connected || busy || !node) return;
    const next = rooms.find(key => contacts().some(contact => roomKey(contact) === key) && (due.get(key) || 0) <= Date.now());
    if (next) void poll(next);
  }, 15000);
  function noteMessage(key) { if (state.selected === key) $("room-poll-status").textContent = "Room message received. More retained posts may arrive while connected."; }
  return {targets, render, noteMessage};
}
