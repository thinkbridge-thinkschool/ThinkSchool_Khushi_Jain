#!/usr/bin/env bash
# Replaces whatever is in the dev database with a curated set of real quotes.
#
# The API seeds a starter account but never seeds quotes, so a fresh database
# leaves every list empty, and a database used for form testing fills up with
# scratch rows. This clears what it can and inserts the set below.
#
#   export QUOTES_EMAIL=...      # the same value as Seed:AdminEmail
#   export QUOTES_PASSWORD=...   # the same value as Seed:AdminPassword
#   ./seed-quotes.sh             # clear, then insert
#   ./seed-quotes.sh --keep      # insert without clearing
#
# Clearing goes through DELETE /api/quotes/{id}, which the API only allows on
# quotes you own -- CanModifyOwnQuoteHandler compares OwnerId to your subject
# id. Rows owned by anyone else answer 403 and are left alone and reported.
set -euo pipefail

API="${QUOTES_API:-http://localhost:5104}"
PURGE=1

if [ "${1:-}" = "--keep" ]; then
  PURGE=0
fi

# Falls back to the same local file the app signs in with, so the credentials
# never have to be typed onto a command line.
CONFIG="public/dev-session.json"

if [ -z "${QUOTES_EMAIL:-}" ] && [ -f "${CONFIG}" ]; then
  QUOTES_EMAIL=$(grep -o '"email"[[:space:]]*:[[:space:]]*"[^"]*"' "${CONFIG}" | cut -d'"' -f4)
  QUOTES_PASSWORD=$(grep -o '"password"[[:space:]]*:[[:space:]]*"[^"]*"' "${CONFIG}" | cut -d'"' -f4)
fi

if [ -z "${QUOTES_EMAIL:-}" ] || [ -z "${QUOTES_PASSWORD:-}" ]; then
  echo "Set QUOTES_EMAIL and QUOTES_PASSWORD, or create ${CONFIG}." >&2
  exit 1
fi

token=$(
  curl -sS -X POST "${API}/api/auth/login" \
    -H 'Content-Type: application/json' \
    -d "{\"email\":\"${QUOTES_EMAIL}\",\"password\":\"${QUOTES_PASSWORD}\"}" |
    grep -o '"access_token":"[^"]*' | cut -d'"' -f4
)

if [ -z "${token}" ]; then
  echo "Could not sign in. Check the API is running and the credentials are right." >&2
  exit 1
fi

# "id":N matches only the real id field: "ownerId" carries a capital I, so the
# literal sequence quote-i-d-colon never appears inside it.
current_ids() {
  curl -sS "${API}/api/quotes?page=1&size=50" \
    -H "Authorization: Bearer ${token}" |
    grep -o '"id":[0-9]*' | cut -d: -f2
}

deleted=0
refused=0

if [ "${PURGE}" -eq 1 ]; then
  echo "Clearing existing quotes…"

  # A pass that deletes nothing means everything left is owned by someone else,
  # so the loop stops rather than spinning on rows it can never remove.
  while true; do
    progressed=0

    for id in $(current_ids); do
      status=$(
        curl -sS -o /dev/null -w '%{http_code}' -X DELETE "${API}/api/quotes/${id}" \
          -H "Authorization: Bearer ${token}"
      )

      case "${status}" in
        204)
          deleted=$((deleted + 1))
          progressed=1
          ;;
        403)
          refused=$((refused + 1))
          ;;
        *)
          echo "  quote ${id}: unexpected ${status}" >&2
          ;;
      esac
    done

    if [ "${progressed}" -eq 0 ]; then
      break
    fi
  done

  echo "  removed ${deleted}"

  if [ "${refused}" -gt 0 ]; then
    echo "  left alone ${refused} owned by another account (403 on delete)"
  fi
fi

post_quote() {
  local author="$1" text="$2"
  local status

  status=$(
    curl -sS -o /dev/null -w '%{http_code}' -X POST "${API}/api/quotes" \
      -H 'Content-Type: application/json' \
      -H "Authorization: Bearer ${token}" \
      -d "{\"author\":\"${author}\",\"text\":\"${text}\"}"
  )

  if [ "${status}" != "201" ]; then
    echo "  ${status}  ${author}" >&2
    return
  fi

  echo "  201  ${author}"
}

echo "Inserting quotes…"

post_quote "Grace Hopper" "The most dangerous phrase in the language is, we have always done it this way."
post_quote "Edsger W. Dijkstra" "Simplicity is prerequisite for reliability."
post_quote "Edsger W. Dijkstra" "Testing shows the presence, not the absence of bugs."
post_quote "Alan Kay" "The best way to predict the future is to invent it."
post_quote "Donald Knuth" "Premature optimization is the root of all evil."
post_quote "Fred Brooks" "Adding manpower to a late software project makes it later."
post_quote "Leslie Lamport" "A distributed system is one in which the failure of a computer you did not know existed can render your own computer unusable."
post_quote "Barbara Liskov" "Modularity based on abstraction is the way things get done."
post_quote "C. A. R. Hoare" "There are two ways of constructing a software design: one way is to make it so simple that there are obviously no deficiencies; the other is to make it so complicated that there are no obvious deficiencies."
post_quote "Martin Fowler" "Any fool can write code that a computer can understand. Good programmers write code that humans can understand."
post_quote "Melvin Conway" "Organizations which design systems are constrained to produce designs which are copies of the communication structures of these organizations."
post_quote "Linus Torvalds" "Talk is cheap. Show me the code."
post_quote "Phil Karlton" "There are only two hard things in computer science: cache invalidation and naming things."
post_quote "Bjarne Stroustrup" "There are only two kinds of languages: the ones people complain about and the ones nobody uses."
post_quote "Alan Perlis" "Fools ignore complexity. Pragmatists suffer it. Some can avoid it. Geniuses remove it."
post_quote "David Wheeler" "All problems in computer science can be solved by another level of indirection."
post_quote "Michael Feathers" "Legacy code is simply code without tests."
post_quote "Ada Lovelace" "The Analytical Engine weaves algebraical patterns just as the Jacquard loom weaves flowers and leaves."
post_quote "Kent Beck" "Make it work, make it right, make it fast."
post_quote "John Gall" "A complex system that works is invariably found to have evolved from a simple system that worked."

echo
echo "Done. Reload http://localhost:4217/quotes"
