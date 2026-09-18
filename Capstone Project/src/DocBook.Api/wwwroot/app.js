// The token is held here and nowhere else, so a refresh signs out and nothing survives the tab.
const session = {
    token: null,
    role: null
};

// DocBook stores no doctor records, so these names live here and only the id ever reaches the API.
const DOCTORS = [
    { id: 'd0c70001-0000-4000-8000-000000000001', name: 'Dr. A. Rao — general practice' },
    { id: 'd0c70002-0000-4000-8000-000000000002', name: 'Dr. S. Iyer — paediatrics' },
    { id: 'd0c70003-0000-4000-8000-000000000003', name: 'Dr. M. Khan — dermatology' },
    { id: 'd0c70004-0000-4000-8000-000000000004', name: 'Dr. L. Fernandes — cardiology' },
    { id: 'd0c70005-0000-4000-8000-000000000005', name: 'Dr. P. Banerjee — orthopaedics' },
    { id: 'd0c70006-0000-4000-8000-000000000006', name: 'Dr. N. Gupta — ophthalmology' },
    { id: 'd0c70007-0000-4000-8000-000000000007', name: 'Dr. R. Menon — ear, nose and throat' },
    { id: 'd0c70008-0000-4000-8000-000000000008', name: 'Dr. T. Desai — neurology' },
    { id: 'd0c70009-0000-4000-8000-000000000009', name: 'Dr. V. Joshi — endocrinology' },
    { id: 'd0c70010-0000-4000-8000-000000000010', name: 'Dr. K. Nair — psychiatry' },
    { id: 'd0c70011-0000-4000-8000-000000000011', name: 'Dr. H. Pillai — gastroenterology' },
    { id: 'd0c70012-0000-4000-8000-000000000012', name: 'Dr. B. Saxena — pulmonology' }
];

const $ = (id) => document.getElementById(id);

const doctor = () => $('doctor').value;

// UTC everywhere, because the API is: a local time here would disagree with every response below.
const at = (value) => new Date(value).toISOString().slice(11, 16);
const on = (value) => new Date(value).toISOString().slice(0, 10);

function say(text) {
    $('message').textContent = text;
}

async function attempt(action) {
    try {
        say('');
        await action();
    } catch (error) {
        say(error.message);
    }
}

async function call(method, path, body) {
    const headers = {};

    if (body !== undefined) {
        headers['Content-Type'] = 'application/json';
    }

    if (session.token) {
        headers.Authorization = `Bearer ${session.token}`;
    }

    const response = await fetch(path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body)
    });

    const text = await response.text();

    if (!response.ok) {
        const failure = new Error(titleOf(text) ?? `The API answered ${response.status}.`);
        failure.status = response.status;

        throw failure;
    }

    return text ? JSON.parse(text) : null;
}

// A refusal comes back as problem details, whose title is the one sentence worth showing.
function titleOf(text) {
    try {
        return JSON.parse(text).title ?? null;
    } catch {
        return null;
    }
}

// Read to decide what to render. The staff policy is enforced by the API whatever this says.
function roleOf(token) {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const padded = payload.padEnd(Math.ceil(payload.length / 4) * 4, '=');

    return JSON.parse(atob(padded)).role;
}

// Both forms are emptied on every switch, so nothing typed earlier is still sitting in a box.
function showGate(registering) {
    $('sign-in-panel').hidden = registering;
    $('register-panel').hidden = !registering;

    $('sign-in-form').reset();
    $('register-form').reset();
}

function showSignedOut() {
    session.token = null;
    session.role = null;

    for (const id of ['identity', 'day', 'staff', 'slots', 'appointments']) {
        $(id).hidden = true;
    }

    $('anonymous').hidden = false;
    $('slot-list').replaceChildren();
    $('appointment-list').replaceChildren();

    showGate(false);
}

async function showSignedIn() {
    const staff = session.role === 'Staff';

    $('anonymous').hidden = true;
    $('identity').hidden = false;
    $('day').hidden = false;
    $('staff').hidden = !staff;
    $('slots').hidden = false;
    $('appointments').hidden = false;

    $('identity-role').textContent = session.role;

    // Clinic staff are not patients, so there is no record of them to read.
    $('identity-name').textContent = staff
        ? 'Clinic desk'
        : (await call('GET', '/api/v1/patients/me')).fullName;

    await loadAppointments();
}

async function findSlots() {
    const list = $('slot-list');
    let found;

    try {
        found = await call(
            'GET',
            `/api/v1/doctors/${doctor()}/days/${$('date').value}/free-slots?minutes=${$('minutes').value}`);
    } catch (error) {
        if (error.status !== 404) {
            throw error;
        }

        // Nothing has been opened for that doctor on that date, which only staff can do.
        list.replaceChildren(row('No day is open for that doctor. Clinic staff open one first.'));

        return;
    }

    list.replaceChildren();

    if (found.slots.length === 0) {
        list.append(row('Nothing free on that day.'));
        return;
    }

    for (const slot of found.slots) {
        list.append(row(`${at(slot.start)} - ${at(slot.end)}`, 'Book', () => bookSlot(slot)));
    }
}

async function bookSlot(slot) {
    await call('POST', '/api/v1/appointments', {
        doctorId: doctor(),
        start: slot.start,
        end: slot.end,
        reason: $('reason').value
    });

    await loadAppointments();
    await findSlots();
}

async function loadAppointments() {
    const page = await call('GET', '/api/v1/appointments/mine');
    const list = $('appointment-list');

    list.replaceChildren();

    if (page.items.length === 0) {
        list.append(row('No appointments yet.'));
        return;
    }

    for (const appointment of page.items) {
        const label =
            `${on(appointment.start)} ${at(appointment.start)}-${at(appointment.end)}` +
            ` · ${appointment.status} · ${appointment.reason}`;

        list.append(appointment.status === 'Booked'
            ? row(label, 'Cancel', () => cancelAppointment(appointment))
            : row(label));
    }
}

async function cancelAppointment(appointment) {
    const reason = prompt('Why is this appointment being cancelled?');

    if (!reason) {
        return;
    }

    await call('POST', `/api/v1/appointments/${appointment.appointmentId}/cancellation`, { reason });
    await loadAppointments();
}

function row(text, actionLabel, action) {
    const item = document.createElement('li');
    const label = document.createElement('span');

    label.textContent = text;
    item.append(label);

    if (actionLabel) {
        const button = document.createElement('button');

        button.type = 'button';
        button.textContent = actionLabel;
        button.addEventListener('click', () => attempt(action));

        item.append(button);
    }

    return item;
}

$('register-form').addEventListener('submit', (event) => {
    event.preventDefault();

    attempt(async () => {
        await call('POST', '/api/v1/patients', {
            fullName: $('register-name').value,
            email: $('register-email').value,
            phone: $('register-phone').value || null,
            password: $('register-password').value
        });

        showGate(false);
        say('Account created. Sign in with those details.');
    });
});

$('show-register').addEventListener('click', () => {
    showGate(true);
    say('');
});

$('show-sign-in').addEventListener('click', () => {
    showGate(false);
    say('');
});

$('sign-in-form').addEventListener('submit', (event) => {
    event.preventDefault();

    attempt(async () => {
        const issued = await call('POST', '/api/v1/tokens', {
            email: $('sign-in-email').value,
            password: $('sign-in-password').value
        });

        session.token = issued.access_token;
        session.role = roleOf(issued.access_token);

        $('sign-in-form').reset();
        await showSignedIn();
    });
});

$('sign-out').addEventListener('click', () => {
    showSignedOut();
    say('Signed out.');
});

$('open-day-form').addEventListener('submit', (event) => {
    event.preventDefault();

    attempt(async () => {
        const opened = await call('POST', `/api/v1/doctors/${doctor()}/days`, {
            date: $('date').value,
            opensAt: $('opens-at').value,
            closesAt: $('closes-at').value
        });

        say(`Day opened as schedule ${opened.scheduleId}.`);
    });
});

$('slots-form').addEventListener('submit', (event) => {
    event.preventDefault();
    attempt(findSlots);
});

$('refresh-appointments').addEventListener('click', () => attempt(loadAppointments));

$('doctor').replaceChildren(...DOCTORS.map(entry => new Option(entry.name, entry.id)));

// Tomorrow, because a slot has to start in the future and most of today already has not.
$('date').value = on(Date.now() + 86_400_000);

showSignedOut();
