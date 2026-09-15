#!/usr/bin/env bash
set -euo pipefail

# Drives the booking flow end to end: register, sign in, open a day, list slots, book, read it back.

BASE_URL="${1:-http://localhost:5205}"
STAFF_EMAIL="${STAFF_EMAIL:-desk@docbook.example}"
STAFF_PASSWORD="${STAFF_PASSWORD:?set STAFF_PASSWORD to the password behind Staff:PasswordHash}"

# A fresh patient each run, because registering an address that is already taken creates nothing.
PATIENT_EMAIL="patient-$(date -u +%s)@docbook.example"
PATIENT_PASSWORD="$(openssl rand -base64 18)"

# There is no doctor aggregate, so any id names a doctor the clinic can open a day for.
DOCTOR_ID="$(openssl rand -hex 16 | sed 's/\(.\{8\}\)\(.\{4\}\)\(.\{4\}\)\(.\{4\}\)\(.\{12\}\)/\1-\2-\3-\4-\5/')"
DATE="$(date -u -d tomorrow +%Y-%m-%d)"

status=''
payload=''

# Leaves the body in $payload, and stops on any status but the one the flow expects.
call() {
    local method=$1 path=$2 expected=$3 body=${4:-} token=${5:-}
    local request=(--silent --show-error -X "$method" "$BASE_URL$path" -H 'Content-Type: application/json')

    if [ -n "$token" ]; then
        request+=(-H "Authorization: Bearer $token")
    fi

    if [ -n "$body" ]; then
        request+=(--data "$body")
    fi

    local response
    response=$(curl "${request[@]}" --write-out $'\n%{http_code}')
    status=${response##*$'\n'}
    payload=${response%$'\n'*}

    printf '\n%s %s -> %s\n' "$method" "$path" "$status"

    if [ -n "$payload" ]; then
        printf '%s\n' "$payload"
    fi

    if [ "$status" != "$expected" ]; then
        printf 'Expected %s.\n' "$expected" >&2
        exit 1
    fi
}

field() {
    grep -o "\"$1\":\"[^\"]*" <<< "$payload" | head -1 | cut -d'"' -f4
}

call POST /api/v1/patients 202 \
    "{\"fullName\":\"Walkthrough Patient\",\"email\":\"$PATIENT_EMAIL\",\"phone\":\"0000000000\",\"password\":\"$PATIENT_PASSWORD\"}"

call POST /api/v1/tokens 200 "{\"email\":\"$PATIENT_EMAIL\",\"password\":\"$PATIENT_PASSWORD\"}"
patient_token=$(field access_token)

call GET /api/v1/patients/me 200 '' "$patient_token"

call POST /api/v1/tokens 200 "{\"email\":\"$STAFF_EMAIL\",\"password\":\"$STAFF_PASSWORD\"}"
staff_token=$(field access_token)

call POST "/api/v1/doctors/$DOCTOR_ID/days" 201 \
    "{\"date\":\"$DATE\",\"opensAt\":\"09:00:00\",\"closesAt\":\"17:00:00\"}" "$staff_token"

call GET "/api/v1/doctors/$DOCTOR_ID/days/$DATE/free-slots?minutes=30" 200 '' "$patient_token"
slot_start=$(field start)
slot_end=$(field end)

call POST /api/v1/appointments 201 \
    "{\"doctorId\":\"$DOCTOR_ID\",\"start\":\"$slot_start\",\"end\":\"$slot_end\",\"reason\":\"Routine consultation\"}" \
    "$patient_token"
appointment_id=$(field appointmentId)

call GET /api/v1/appointments/mine 200 '' "$patient_token"

# The confirmation is written to the outbox by the booking and delivered on the dispatcher's next pass.
sleep 8

printf '\nBooked %s at %s.\n' "$appointment_id" "$slot_start"
printf 'The confirmation is in the API log: "Notification Your appointment is confirmed queued".\n'
