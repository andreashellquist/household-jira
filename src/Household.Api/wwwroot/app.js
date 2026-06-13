// Hemma — household board, shopping list, meal planner.

const CATEGORIES = {
  kok:      { label: "Kök",      color: "#F59E0B" },
  stad:     { label: "Städ",     color: "#38BDF8" },
  tvatt:    { label: "Tvätt",    color: "#A78BFA" },
  tradgard: { label: "Trädgård", color: "#34D399" },
  fix:      { label: "Fix",      color: "#60A5FA" },
  barn:     { label: "Barn",     color: "#F472B6" },
  husdjur:  { label: "Husdjur",  color: "#FB923C" },
  arenden:  { label: "Ärenden",  color: "#FACC15" },
  other:    { label: "Övrigt",   color: "#94A3B8" },
};
const PRIORITIES = { low: "Lugnt", normal: "Denna vecka", urgent: "Bråttom" };
const COLUMNS = [
  { status: "todo",  label: "Att göra" },
  { status: "doing", label: "Pågår" },
  { status: "done",  label: "Klart" },
];
const MEMBER_COLORS = ["#F59E0B", "#38BDF8", "#F472B6", "#34D399", "#A78BFA", "#FB923C"];
const DOW = ["sön", "mån", "tis", "ons", "tor", "fre", "lör"];

const state = { members: [], chores: [], shopping: [], meals: [], filter: null, editing: null };
const $ = (id) => document.getElementById(id);

const api = async (url, method = "GET", body) => {
  const res = await fetch(url, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new Error(`${method} ${url} → ${res.status}`);
  return res.status === 204 ? null : res.json();
};

const todayISO = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};

function toast(msg) {
  const t = $("toast");
  t.textContent = msg;
  t.classList.add("show");
  clearTimeout(t._timer);
  t._timer = setTimeout(() => t.classList.remove("show"), 1800);
}

// ---------- Board ----------
function memberById(id) { return state.members.find((m) => m.id === id); }

function avatarHtml(member) {
  if (!member) return "";
  const initials = member.name.trim().split(/\s+/).map((w) => w[0]).slice(0, 2).join("").toUpperCase();
  return `<span class="avatar" style="background:${member.color}">${initials}</span>`;
}

function dueLabel(iso) {
  const today = todayISO();
  if (iso === today) return { text: "Idag", overdue: false };
  if (iso < today) return { text: "Försenad", overdue: true };
  const d = new Date(iso + "T00:00");
  const diff = Math.round((d - new Date(today + "T00:00")) / 86400000);
  if (diff === 1) return { text: "Imorgon", overdue: false };
  if (diff < 7) return { text: DOW[d.getDay()].charAt(0).toUpperCase() + DOW[d.getDay()].slice(1), overdue: false };
  return { text: `${d.getDate()}/${d.getMonth() + 1}`, overdue: false };
}

function renderCatFilter() {
  $("catFilter").innerHTML = Object.entries(CATEGORIES)
    .map(([key, c]) => `
      <button class="chip ${state.filter === key ? "on" : ""}" data-cat="${key}">
        <span class="dot" style="background:${c.color}"></span>${c.label}
      </button>`)
    .join("");
}

function cardHtml(chore) {
  const cat = CATEGORIES[chore.category] ?? CATEGORIES.other;
  const tags = [];
  if (chore.priority === "urgent") tags.push(`<span class="tag urgent">● ${PRIORITIES.urgent}</span>`);
  if (chore.dueDate) {
    const due = dueLabel(chore.dueDate);
    tags.push(`<span class="tag ${due.overdue && chore.status !== "done" ? "overdue" : ""}">📅 ${due.text}</span>`);
  }
  if (chore.recurDays) tags.push(`<span class="tag">↻</span>`);
  tags.push(`<span class="tag" style="color:${cat.color}">${cat.label}</span>`);
  const next = chore.status === "todo" ? "doing" : chore.status === "doing" ? "done" : null;
  return `
    <div class="card ${chore.status}" data-id="${chore.id}" style="border-left-color:${cat.color}">
      <div class="title">${esc(chore.title)}</div>
      <div class="meta">${tags.join("")}${avatarHtml(memberById(chore.assigneeId))}</div>
      ${next ? `<div class="advance"><button class="advance-btn" data-advance="${next}" aria-label="Flytta fram">✓</button></div>` : ""}
    </div>`;
}

function renderBoard() {
  renderCatFilter();
  const chores = state.filter ? state.chores.filter((c) => c.category === state.filter) : state.chores;
  $("board").innerHTML = COLUMNS.map((col) => {
    const items = chores.filter((c) => c.status === col.status);
    return `
      <div class="col">
        <div class="col-head">${col.label}<span class="count">${items.length}</span></div>
        <div class="col-body">
          ${items.map(cardHtml).join("") || `<div class="empty">${col.status === "done" ? "Inget klart ännu" : "Tomt här ✨"}</div>`}
        </div>
      </div>`;
  }).join("");
}

async function advanceChore(id, status) {
  const chore = state.chores.find((c) => c.id === id);
  chore.status = status; // optimistic
  renderBoard();
  const { next } = await api(`/api/chores/${id}/status`, "POST", { status });
  if (next) {
    chore.recurDays = null;
    state.chores.push(next);
    toast(`↻ Ny "${next.title}" skapad till ${next.dueDate}`);
    renderBoard();
  } else if (status === "done") {
    toast("Snyggt jobbat! 🎉");
  }
}

// ---------- Chore sheet ----------
const form = { category: "other", priority: "normal", assigneeId: null };

function openSheet(chore) {
  state.editing = chore ?? null;
  form.category = chore?.category ?? state.filter ?? "other";
  form.priority = chore?.priority ?? "normal";
  form.assigneeId = chore?.assigneeId ?? null;
  $("sheetTitle").textContent = chore ? "Redigera uppgift" : "Ny uppgift";
  $("fTitle").value = chore?.title ?? "";
  $("fDue").value = chore?.dueDate ?? "";
  $("fRecur").value = chore?.recurDays ?? "";
  $("fNotes").value = chore?.notes ?? "";
  $("fDelete").hidden = !chore;
  renderSheetPickers();
  $("backdrop").classList.add("open");
  $("choreSheet").classList.add("open");
  if (!chore) setTimeout(() => $("fTitle").focus(), 100);
}

function closeSheet() {
  $("backdrop").classList.remove("open");
  $("choreSheet").classList.remove("open");
}

function renderSheetPickers() {
  $("fCategory").innerHTML = Object.entries(CATEGORIES)
    .map(([key, c]) => `
      <button class="chip ${form.category === key ? "on" : ""}" data-pick-cat="${key}">
        <span class="dot" style="background:${c.color}"></span>${c.label}
      </button>`)
    .join("");
  $("fPriority").innerHTML = Object.entries(PRIORITIES)
    .map(([key, label]) => `<button class="${form.priority === key ? "on" : ""}" data-pick-pri="${key}">${label}</button>`)
    .join("");
  $("fAssignee").innerHTML =
    `<button class="chip ${form.assigneeId === null ? "on" : ""}" data-pick-mem="">Ingen</button>` +
    state.members
      .map((m) => `
        <button class="chip ${form.assigneeId === m.id ? "on" : ""}" data-pick-mem="${m.id}">
          <span class="dot" style="background:${m.color}"></span>${esc(m.name)}
        </button>`)
      .join("");
}

async function saveChore() {
  const title = $("fTitle").value.trim();
  if (!title) { $("fTitle").focus(); return; }
  const payload = {
    title,
    notes: $("fNotes").value.trim() || null,
    category: form.category,
    priority: form.priority,
    assigneeId: form.assigneeId,
    dueDate: $("fDue").value || null,
    recurDays: $("fRecur").value ? Number($("fRecur").value) : null,
  };
  if (state.editing) {
    const updated = await api(`/api/chores/${state.editing.id}`, "PUT", { ...payload, status: state.editing.status });
    Object.assign(state.editing, updated);
  } else {
    state.chores.push(await api("/api/chores", "POST", { ...payload, status: "todo" }));
  }
  closeSheet();
  renderBoard();
}

async function deleteChore() {
  await api(`/api/chores/${state.editing.id}`, "DELETE");
  state.chores = state.chores.filter((c) => c.id !== state.editing.id);
  closeSheet();
  renderBoard();
}

// ---------- Shopping ----------
function renderShopping() {
  const open = state.shopping.filter((i) => !i.checked);
  const done = state.shopping.filter((i) => i.checked);
  const row = (i) => `
    <div class="row ${i.checked ? "checked" : ""}" data-shop="${i.id}">
      <span class="checkbox">${i.checked ? "✓" : ""}</span>
      <div class="grow">${esc(i.name)}${i.qty ? ` <span class="sub">${esc(i.qty)}</span>` : ""}</div>
      <button class="del" data-shop-del="${i.id}" aria-label="Ta bort">×</button>
    </div>`;
  $("shopList").innerHTML =
    (open.map(row).join("") || `<div class="empty">Inköpslistan är tom 🛒</div>`) +
    done.map(row).join("") +
    (done.length ? `<button class="section-action" id="clearChecked">Rensa avbockade (${done.length})</button>` : "");
  const badge = $("shopBadge");
  badge.hidden = open.length === 0;
  badge.textContent = open.length;
}

// ---------- Meals ----------
function renderMeals() {
  const days = [];
  const now = new Date();
  for (let i = 0; i < 7; i++) {
    const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() + i);
    const iso = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
    days.push({ iso, d });
  }
  $("mealList").innerHTML = days
    .map(({ iso, d }) => {
      const meal = state.meals.find((m) => m.date === iso && m.slot === "dinner");
      return `
        <div class="meal-day ${iso === todayISO() ? "today" : ""}">
          <div class="day"><span class="dow">${DOW[d.getDay()]}</span><span class="dom">${d.getDate()}</span></div>
          <input data-meal="${iso}" value="${esc(meal?.title ?? "")}" placeholder="Vad blir det till middag?" autocomplete="off">
        </div>`;
    })
    .join("");
}

async function saveMeal(iso, title) {
  const existing = state.meals.find((m) => m.date === iso && m.slot === "dinner");
  if ((existing?.title ?? "") === title.trim()) return;
  const result = await api("/api/meals", "PUT", { date: iso, slot: "dinner", title: title.trim() });
  state.meals = state.meals.filter((m) => !(m.date === iso && m.slot === "dinner"));
  if (result) state.meals.push(result);
  toast("Matplan sparad");
}

// ---------- Family ----------
function renderFamily() {
  const weekAgo = Date.now() - 7 * 86400000;
  const doneThisWeek = state.chores.filter((c) => c.status === "done" && c.completedAt && new Date(c.completedAt) > weekAgo);
  const openCount = state.chores.filter((c) => c.status !== "done").length;
  $("famStats").innerHTML = `
    <div class="stat"><div class="num">${openCount}</div><div class="lbl">Öppna uppgifter</div></div>
    <div class="stat"><div class="num">${doneThisWeek.length}</div><div class="lbl">Klara denna vecka</div></div>`;
  $("memberList").innerHTML =
    state.members
      .map((m) => {
        const count = doneThisWeek.filter((c) => c.assigneeId === m.id).length;
        return `
          <div class="row">
            ${avatarHtml(m)}
            <div class="grow">${esc(m.name)}<div class="sub">${count} klara denna vecka</div></div>
            <button class="del" data-mem-del="${m.id}" aria-label="Ta bort">×</button>
          </div>`;
      })
      .join("") || `<div class="empty">Lägg till er i familjen så kan ni tilldela uppgifter 👆</div>`;
}

// ---------- Wiring ----------
function esc(s) {
  return String(s).replace(/[&<>"']/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[ch]));
}

const RENDERERS = { board: renderBoard, shop: renderShopping, meals: renderMeals, family: renderFamily };

function switchView(name) {
  document.querySelectorAll(".view").forEach((v) => v.classList.toggle("active", v.id === `view-${name}`));
  document.querySelectorAll("nav button").forEach((b) => b.classList.toggle("active", b.dataset.view === name));
  RENDERERS[name]?.();
}

document.addEventListener("click", async (e) => {
  const t = e.target.closest("[data-view], [data-cat], [data-advance], .card, [data-shop-del], [data-shop], [data-mem-del], [data-pick-cat], [data-pick-pri], [data-pick-mem], #clearChecked");
  if (!t) return;

  if (t.dataset.view) return switchView(t.dataset.view);
  if (t.dataset.cat !== undefined) {
    state.filter = state.filter === t.dataset.cat ? null : t.dataset.cat;
    return renderBoard();
  }
  if (t.dataset.advance) {
    e.stopPropagation();
    return advanceChore(Number(t.closest(".card").dataset.id), t.dataset.advance);
  }
  if (t.classList.contains("card")) {
    return openSheet(state.chores.find((c) => c.id === Number(t.dataset.id)));
  }
  if (t.dataset.pickCat) { form.category = t.dataset.pickCat; return renderSheetPickers(); }
  if (t.dataset.pickPri) { form.priority = t.dataset.pickPri; return renderSheetPickers(); }
  if (t.dataset.pickMem !== undefined) {
    form.assigneeId = t.dataset.pickMem === "" ? null : Number(t.dataset.pickMem);
    return renderSheetPickers();
  }
  if (t.dataset.shopDel) {
    e.stopPropagation();
    await api(`/api/shopping/${t.dataset.shopDel}`, "DELETE");
    state.shopping = state.shopping.filter((i) => i.id !== Number(t.dataset.shopDel));
    return renderShopping();
  }
  if (t.id === "clearChecked") {
    await api("/api/shopping/clear-checked", "POST");
    state.shopping = state.shopping.filter((i) => !i.checked);
    return renderShopping();
  }
  if (t.dataset.shop) {
    const item = state.shopping.find((i) => i.id === Number(t.dataset.shop));
    item.checked = !item.checked; // optimistic
    renderShopping();
    return api(`/api/shopping/${item.id}/toggle`, "POST");
  }
  if (t.dataset.memDel) {
    await api(`/api/members/${t.dataset.memDel}`, "DELETE");
    state.members = state.members.filter((m) => m.id !== Number(t.dataset.memDel));
    state.chores.forEach((c) => { if (c.assigneeId === Number(t.dataset.memDel)) c.assigneeId = null; });
    renderFamily();
    return renderBoard();
  }
});

$("fabAdd").addEventListener("click", () => openSheet(null));
$("backdrop").addEventListener("click", closeSheet);
$("fSave").addEventListener("click", saveChore);
$("fDelete").addEventListener("click", deleteChore);

$("shopForm").addEventListener("submit", async (e) => {
  e.preventDefault();
  const name = $("shopName").value.trim();
  if (!name) return;
  const item = await api("/api/shopping", "POST", { name, qty: $("shopQty").value.trim() || null });
  state.shopping.unshift(item);
  $("shopName").value = "";
  $("shopQty").value = "";
  $("shopName").focus();
  renderShopping();
});

$("memberForm").addEventListener("submit", async (e) => {
  e.preventDefault();
  const name = $("memberName").value.trim();
  if (!name) return;
  const color = MEMBER_COLORS[state.members.length % MEMBER_COLORS.length];
  const member = await api("/api/members", "POST", { name, color });
  state.members.push(member);
  $("memberName").value = "";
  renderFamily();
});

$("mealList").addEventListener("change", (e) => {
  if (e.target.dataset.meal) saveMeal(e.target.dataset.meal, e.target.value);
});
$("mealList").addEventListener("keydown", (e) => {
  if (e.key === "Enter" && e.target.dataset.meal) e.target.blur();
});

// ---------- Init ----------
(async function init() {
  const d = new Date();
  $("todayLabel").textContent = d.toLocaleDateString("sv-SE", { weekday: "long", day: "numeric", month: "long" });
  const data = await api("/api/bootstrap");
  Object.assign(state, { members: data.members, chores: data.chores, shopping: data.shopping, meals: data.meals });
  renderBoard();
  renderShopping();
  renderMeals();
  renderFamily();
})();
