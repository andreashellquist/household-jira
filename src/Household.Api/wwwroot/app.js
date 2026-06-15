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
const UNITS = ["st", "g", "kg", "dl", "l", "msk", "tsk", "krm", "förp", "klyfta", "näve"];

const state = {
  members: [], chores: [], shopping: [], meals: [], recipes: [],
  filter: null, editing: null,
  me: Number(localStorage.getItem("hemma.me")) || null, // who's using this device
  mine: false, // "Mitt" filter on the board
  icaConfigured: false,
  editingRecipe: null,
  mealPickDate: null,
};
const $ = (id) => document.getElementById(id);

function setMe(id) {
  state.me = id;
  if (id) localStorage.setItem("hemma.me", id);
  else { localStorage.removeItem("hemma.me"); state.mine = false; }
}

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
  const mineChip = `
    <button class="chip mine ${state.mine ? "on" : ""}" data-mine>
      <span class="dot" style="background:${memberById(state.me)?.color ?? "var(--text-dim)"}"></span>Mitt
    </button>`;
  $("catFilter").innerHTML = mineChip + Object.entries(CATEGORIES)
    .map(([key, c]) => `
      <button class="chip ${state.filter === key ? "on" : ""}" data-cat="${key}">
        <span class="dot" style="background:${c.color}"></span>${c.label}
      </button>`)
    .join("");
}

// Open chores that are due today or earlier — drives the "Tavla" nudge badge.
function overdueCount() {
  const today = todayISO();
  return state.chores.filter((c) => c.status !== "done" && c.dueDate && c.dueDate <= today).length;
}

function renderBoardBadge() {
  const n = overdueCount();
  const badge = $("boardBadge");
  badge.hidden = n === 0;
  badge.textContent = n;
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
  renderBoardBadge();
  let chores = state.chores;
  if (state.filter) chores = chores.filter((c) => c.category === state.filter);
  if (state.mine && state.me) chores = chores.filter((c) => c.assigneeId === state.me);
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
    chore.rotate = false;
    state.chores.push(next);
    const who = memberById(next.assigneeId);
    toast(who ? `↻ Nästa gång: ${who.name}` : `↻ Ny uppgift skapad`);
    renderBoard();
  } else if (status === "done") {
    toast("Snyggt jobbat! 🎉");
    renderBoard();
  }
}

// ---------- Chore sheet ----------
const form = { category: "other", priority: "normal", assigneeId: null };

function openSheet(chore) {
  state.editing = chore ?? null;
  form.category = chore?.category ?? state.filter ?? "other";
  form.priority = chore?.priority ?? "normal";
  form.assigneeId = chore?.assigneeId ?? state.me ?? null;
  $("sheetTitle").textContent = chore ? "Redigera uppgift" : "Ny uppgift";
  $("fTitle").value = chore?.title ?? "";
  $("fDue").value = chore?.dueDate ?? "";
  $("fRecur").value = chore?.recurDays ?? "";
  $("fRotate").checked = chore?.rotate ?? false;
  $("fNotes").value = chore?.notes ?? "";
  $("fDelete").hidden = !chore;
  renderSheetPickers();
  updateRotateVisibility();
  $("backdrop").classList.add("open");
  $("choreSheet").classList.add("open");
  if (!chore) setTimeout(() => $("fTitle").focus(), 100);
}

// "Turas om" only makes sense for a recurring chore with at least two people.
function updateRotateVisibility() {
  const show = $("fRecur").value !== "" && state.members.length >= 2;
  $("fRotateField").hidden = !show;
  if (!show) $("fRotate").checked = false;
}

function closeSheet() {
  $("backdrop").classList.remove("open");
  document.querySelectorAll(".sheet.open").forEach((s) => s.classList.remove("open"));
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
    rotate: $("fRecur").value !== "" && $("fRotate").checked,
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

// ---------- Identity ("vem är du?") ----------
function renderMeAvatar() {
  const me = memberById(state.me);
  const btn = $("meAvatar");
  if (me) {
    btn.classList.add("set");
    btn.style.background = me.color;
    btn.textContent = me.name.trim().split(/\s+/).map((w) => w[0]).slice(0, 2).join("").toUpperCase();
  } else {
    btn.classList.remove("set");
    btn.style.background = "";
    btn.textContent = "?";
  }
}

function openMeSheet() {
  $("mePicker").innerHTML =
    state.members
      .map((m) => `
        <button class="chip ${state.me === m.id ? "on" : ""}" data-pick-me="${m.id}">
          <span class="dot" style="background:${m.color}"></span>${esc(m.name)}
        </button>`)
      .join("") +
    `<button class="chip ${state.me === null ? "on" : ""}" data-pick-me="">Ingen</button>` +
    (state.members.length === 0 ? `<div class="empty">Lägg till familjemedlemmar under Familj först 👪</div>` : "");
  $("backdrop").classList.add("open");
  $("meSheet").classList.add("open");
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
      const linked = meal?.recipeId ? "linked" : "";
      return `
        <div class="meal-day ${iso === todayISO() ? "today" : ""}">
          <div class="day"><span class="dow">${DOW[d.getDay()]}</span><span class="dom">${d.getDate()}</span></div>
          <input class="${linked}" data-meal="${iso}" value="${esc(meal?.title ?? "")}" placeholder="Vad blir det till middag?" autocomplete="off">
          <button class="recipe-pick" data-meal-pick="${iso}" aria-label="Välj recept">📖</button>
        </div>`;
    })
    .join("");
}

async function saveMeal(iso, title, recipeId = null) {
  const existing = state.meals.find((m) => m.date === iso && m.slot === "dinner");
  if ((existing?.title ?? "") === title.trim() && (existing?.recipeId ?? null) === recipeId) return;
  const result = await api("/api/meals", "PUT", { date: iso, slot: "dinner", title: title.trim(), recipeId });
  state.meals = state.meals.filter((m) => !(m.date === iso && m.slot === "dinner"));
  if (result) state.meals.push(result);
  toast("Matplan sparad");
}

// ---------- Recipes ----------
function renderRecipes() {
  const q = ($("recipeSearch").value || "").toLowerCase();
  const list = state.recipes.filter((r) => r.name.toLowerCase().includes(q));
  $("recipeList").innerHTML =
    list
      .map((r) => `
        <button class="recipe-card" data-recipe="${r.id}">
          <div class="grow">
            <div class="title">${esc(r.name)}</div>
            <div class="sub">${r.ingredients.length} ingredienser${r.source ? " · " + esc(r.source) : ""}</div>
          </div>
          <span class="chev">›</span>
        </button>`)
      .join("") ||
    `<div class="empty">${state.recipes.length ? "Inga recept matchar" : "Lägg till ditt första recept 👆"}</div>`;
}

function ingredientRowHtml(ing = { name: "", amount: "", unit: "st" }) {
  return `
    <div class="ing-row">
      <input class="ing-name" placeholder="Ingrediens" value="${esc(ing.name)}" autocomplete="off">
      <input class="ing-amt" type="number" inputmode="decimal" placeholder="0" value="${ing.amount ?? ""}">
      <select class="ing-unit">${UNITS.map((u) => `<option ${u === ing.unit ? "selected" : ""}>${u}</option>`).join("")}</select>
      <button type="button" class="ing-del" aria-label="Ta bort">×</button>
    </div>`;
}

function openRecipeSheet(recipe) {
  state.editingRecipe = recipe ?? null;
  $("recipeSheetTitle").textContent = recipe ? "Redigera recept" : "Nytt recept";
  $("rName").value = recipe?.name ?? "";
  $("rSource").value = recipe?.source ?? "";
  $("rInstructions").value = recipe?.instructions ?? "";
  $("rPreparations").value = recipe?.preparations ?? "";
  const ings = recipe?.ingredients?.length ? recipe.ingredients : [undefined];
  $("rIngredients").innerHTML = ings.map((i) => ingredientRowHtml(i)).join("");
  $("rDelete").hidden = !recipe;
  $("backdrop").classList.add("open");
  $("recipeSheet").classList.add("open");
  if (!recipe) setTimeout(() => $("rName").focus(), 100);
}

function collectIngredients() {
  return [...$("rIngredients").querySelectorAll(".ing-row")]
    .map((row) => ({
      name: row.querySelector(".ing-name").value.trim(),
      amount: Number(row.querySelector(".ing-amt").value) || 0,
      unit: row.querySelector(".ing-unit").value,
    }))
    .filter((i) => i.name);
}

async function saveRecipe() {
  const name = $("rName").value.trim();
  if (!name) { $("rName").focus(); return; }
  const payload = {
    name,
    source: $("rSource").value.trim() || null,
    instructions: $("rInstructions").value.trim() || null,
    preparations: $("rPreparations").value.trim() || null,
    ingredients: collectIngredients(),
  };
  if (state.editingRecipe) {
    const updated = await api(`/api/recipes/${state.editingRecipe.id}`, "PUT", payload);
    Object.assign(state.editingRecipe, updated);
  } else {
    state.recipes.push(await api("/api/recipes", "POST", payload));
  }
  closeSheet();
  renderRecipes();
}

async function deleteRecipe() {
  await api(`/api/recipes/${state.editingRecipe.id}`, "DELETE");
  state.recipes = state.recipes.filter((r) => r.id !== state.editingRecipe.id);
  closeSheet();
  renderRecipes();
}

// ---------- Meal → recipe picker ----------
function openMealPick(iso) {
  state.mealPickDate = iso;
  const meal = state.meals.find((m) => m.date === iso && m.slot === "dinner");
  $("mealPickList").innerHTML =
    state.recipes
      .map((r) => `<button class="chip ${meal?.recipeId === r.id ? "on" : ""}" data-pick-recipe="${r.id}">${esc(r.name)}</button>`)
      .join("") +
    (meal?.recipeId ? `<button class="chip" data-pick-recipe="">Koppla bort</button>` : "") +
    (state.recipes.length ? "" : `<div class="empty">Inga recept ännu — lägg till under Recept 📖</div>`);
  $("backdrop").classList.add("open");
  $("mealPickSheet").classList.add("open");
}

async function pickRecipeForMeal(recipeId) {
  const iso = state.mealPickDate;
  const recipe = recipeId ? state.recipes.find((r) => r.id === recipeId) : null;
  await saveMeal(iso, recipe?.name ?? "", recipe?.id ?? null);
  closeSheet();
  renderMeals();
}

// ---------- ICA push ----------
async function pushToIca() {
  toast("Skickar…");
  const res = await api("/api/ica/push", "POST");
  $("icaTitle").textContent = res.title;
  const status = $("icaStatus");
  if (res.sent) {
    status.className = "ica-status ok";
    status.textContent = `✓ Skickat till ICA — ${res.count} varor i "${res.title}"`;
  } else {
    status.className = "ica-status warn";
    status.textContent = res.count === 0 ? "Inget att skicka — planera middagar eller fyll inköpslistan först." : `Kunde inte skicka: ${res.error}`;
  }
  $("icaRows").innerHTML =
    res.rows.map((r) => `<div class="ica-line">${esc(r)}</div>`).join("") ||
    `<div class="empty">Tom lista</div>`;
  $("backdrop").classList.add("open");
  $("icaSheet").classList.add("open");
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

const RENDERERS = { board: renderBoard, shop: renderShopping, recipes: renderRecipes, meals: renderMeals, family: renderFamily };

function switchView(name) {
  document.querySelectorAll(".view").forEach((v) => v.classList.toggle("active", v.id === `view-${name}`));
  document.querySelectorAll("nav button").forEach((b) => b.classList.toggle("active", b.dataset.view === name));
  RENDERERS[name]?.();
}

document.addEventListener("click", async (e) => {
  const t = e.target.closest("[data-view], [data-cat], [data-mine], [data-pick-me], [data-advance], .card, [data-shop-del], [data-shop], [data-mem-del], [data-pick-cat], [data-pick-pri], [data-pick-mem], #clearChecked, [data-recipe], [data-meal-pick], [data-pick-recipe], .ing-del");
  if (!t) return;

  if (t.dataset.recipe) return openRecipeSheet(state.recipes.find((r) => r.id === Number(t.dataset.recipe)));
  if (t.dataset.mealPick) return openMealPick(t.dataset.mealPick);
  if (t.dataset.pickRecipe !== undefined) return pickRecipeForMeal(t.dataset.pickRecipe === "" ? null : Number(t.dataset.pickRecipe));
  if (t.classList.contains("ing-del")) { e.preventDefault(); return t.closest(".ing-row").remove(); }

  if (t.dataset.view) return switchView(t.dataset.view);
  if (t.dataset.cat !== undefined) {
    state.filter = state.filter === t.dataset.cat ? null : t.dataset.cat;
    return renderBoard();
  }
  if (t.dataset.mine !== undefined) {
    if (!state.me) return openMeSheet(); // can't filter "mine" until we know who you are
    state.mine = !state.mine;
    return renderBoard();
  }
  if (t.dataset.pickMe !== undefined) {
    setMe(t.dataset.pickMe === "" ? null : Number(t.dataset.pickMe));
    closeSheet();
    renderMeAvatar();
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
    const memId = Number(t.dataset.memDel);
    await api(`/api/members/${memId}`, "DELETE");
    state.members = state.members.filter((m) => m.id !== memId);
    state.chores.forEach((c) => { if (c.assigneeId === memId) c.assigneeId = null; });
    if (state.me === memId) setMe(null);
    renderFamily();
    renderMeAvatar();
    return renderBoard();
  }
});

$("fabAdd").addEventListener("click", () => openSheet(null));
$("backdrop").addEventListener("click", closeSheet);
$("fSave").addEventListener("click", saveChore);
$("fDelete").addEventListener("click", deleteChore);
$("meAvatar").addEventListener("click", openMeSheet);
$("fRecur").addEventListener("change", updateRotateVisibility);

$("recipeAdd").addEventListener("click", () => openRecipeSheet(null));
$("recipeSearch").addEventListener("input", renderRecipes);
$("rAddIngredient").addEventListener("click", () => {
  $("rIngredients").insertAdjacentHTML("beforeend", ingredientRowHtml());
  $("rIngredients").lastElementChild.querySelector(".ing-name").focus();
});
$("rSave").addEventListener("click", saveRecipe);
$("rDelete").addEventListener("click", deleteRecipe);
$("icaPush").addEventListener("click", pushToIca);

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
  renderMeAvatar();
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
  Object.assign(state, {
    members: data.members, chores: data.chores, shopping: data.shopping,
    meals: data.meals, recipes: data.recipes ?? [], icaConfigured: data.icaConfigured,
  });
  if (state.me && !memberById(state.me)) setMe(null); // stale identity from a removed member
  renderMeAvatar();
  renderBoard();
  renderShopping();
  renderMeals();
  renderRecipes();
  renderFamily();
})();
