"use strict";

const MAX_CENTS = 99999999;
const balanceEl = document.getElementById("balance");
const messageEl = document.getElementById("message");
const forms = document.querySelectorAll("form");

// Exact decimal text -> integer hundredths; returns null instead of rounding.
function parseAmountToCents(text) {
  const match = /^(\d{1,8})(?:[.,](\d{1,2}))?$/.exec(text.trim());
  if (!match) return null;
  const cents = Number(match[1]) * 100 + Number((match[2] ?? "").padEnd(2, "0"));
  return cents <= MAX_CENTS ? cents : null;
}

function formatCents(cents) {
  const whole = Math.floor(cents / 100);
  const fraction = String(cents % 100).padStart(2, "0");
  return `${whole}.${fraction}`;
}

function show(text, isError) {
  messageEl.textContent = text;
  messageEl.className = isError ? "error" : "";
}

function setBusy(busy) {
  forms.forEach((form) => form.querySelectorAll("input,button").forEach((el) => (el.disabled = busy)));
}

const errorTexts = {
  invalid_amount: "Ungültiger Betrag.",
  overflow: "Gutschrift abgelehnt: Der Saldo würde 999999.99 überschreiten.",
  insufficient_funds: "Abbuchung abgelehnt: Nicht ausreichende Deckung.",
};

async function loadBalance() {
  try {
    const response = await fetch("/api/account/balance");
    const body = await response.json();
    balanceEl.textContent = formatCents(body.balanceCents);
  } catch {
    show("Saldo konnte nicht geladen werden.", true);
  }
}

async function book(kind, input) {
  const cents = parseAmountToCents(input.value);
  if (cents === null) {
    show("Bitte einen Betrag mit höchstens zwei Nachkommastellen bis 999999.99 eingeben.", true);
    return;
  }
  setBusy(true);
  try {
    const response = await fetch(`/api/account/${kind}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ amountCents: cents }),
    });
    const body = await response.json();
    if (response.ok) {
      balanceEl.textContent = formatCents(body.balanceCents);
      input.value = "";
      show(kind === "credits" ? "Gutschrift gebucht." : "Abbuchung gebucht.", false);
    } else {
      if (typeof body.balanceCents === "number") balanceEl.textContent = formatCents(body.balanceCents);
      show(errorTexts[body.error] ?? "Buchung fehlgeschlagen.", true);
    }
  } catch {
    // No automatic retry: the booking outcome is unknown, so only reload the balance.
    show("Verbindungsfehler: Ergebnis unbekannt. Bitte Saldo prüfen.", true);
    await loadBalance();
  } finally {
    setBusy(false);
  }
}

document.getElementById("credit-form").addEventListener("submit", (event) => {
  event.preventDefault();
  book("credits", document.getElementById("credit-amount"));
});
document.getElementById("debit-form").addEventListener("submit", (event) => {
  event.preventDefault();
  book("debits", document.getElementById("debit-amount"));
});

loadBalance();
