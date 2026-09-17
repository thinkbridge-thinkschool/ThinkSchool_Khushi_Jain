import http from 'k6/http';
import { check, fail } from 'k6';
import { Trend } from 'k6/metrics';

// k6 runs in a container and the API on the host, which is what host.docker.internal is for.
const BASE_URL = __ENV.BASE_URL || 'http://host.docker.internal:5205';
const STAFF_EMAIL = __ENV.STAFF_EMAIL || 'desk@docbook.example';
const STAFF_PASSWORD = __ENV.STAFF_PASSWORD;

const PATIENT_PASSWORD = 'correct-horse-battery';
const BOOKINGS = 12;

const freeSlots = new Trend('free_slots', true);
const myAppointments = new Trend('my_appointments', true);

export const options = {
    summaryTrendStats: ['min', 'avg', 'med', 'p(95)', 'p(99)', 'max'],

    // A throttled or failing run must not be read as a fast one, so anything but 200 fails the test.
    thresholds: {
        checks: ['rate>0.99'],
    },

    // One after the other: two read paths contending would measure the contention, not the path.
    scenarios: {
        free_slots: {
            executor: 'constant-vus',
            vus: 20,
            duration: '45s',
            exec: 'readFreeSlots',
        },
        my_appointments: {
            executor: 'constant-vus',
            vus: 20,
            duration: '45s',
            startTime: '50s',
            exec: 'readMyAppointments',
        },
    },
};

export function setup() {
    if (!STAFF_PASSWORD) {
        fail('Set STAFF_PASSWORD to the password behind Staff:PasswordHash.');
    }

    const email = `perf-${Date.now()}@docbook.test`;

    post('/api/v1/patients', {
        fullName: 'Perf Patient',
        email,
        phone: '0000000000',
        password: PATIENT_PASSWORD,
    });

    const patient = tokenFor(email, PATIENT_PASSWORD);
    const staff = tokenFor(STAFF_EMAIL, STAFF_PASSWORD);

    const doctorId = uuid();
    const date = tomorrow();

    expect(
        post(`/api/v1/doctors/${doctorId}/days`, { date, opensAt: '09:00:00', closesAt: '17:00:00' }, staff),
        201,
        'open the day');

    const listed = expect(
        get(`/api/v1/doctors/${doctorId}/days/${date}/free-slots?minutes=30`, patient),
        200,
        'list the free slots');

    const slots = JSON.parse(listed.body).slots;

    // A day mostly taken, because an empty one is the case where there is nothing to compute.
    for (let index = 0; index < BOOKINGS; index++) {
        expect(
            post('/api/v1/appointments', {
                doctorId,
                start: slots[index].start,
                end: slots[index].end,
                reason: 'perf',
            }, patient),
            201,
            'book a slot');
    }

    return { patient, doctorId, date };
}

export function readFreeSlots(data) {
    const response = get(
        `/api/v1/doctors/${data.doctorId}/days/${data.date}/free-slots?minutes=30`,
        data.patient);

    freeSlots.add(response.timings.duration);
    check(response, { 'free slots answered 200': answer => answer.status === 200 });
}

export function readMyAppointments(data) {
    const response = get('/api/v1/appointments/mine?page=1&size=20', data.patient);

    myAppointments.add(response.timings.duration);
    check(response, { 'my appointments answered 200': answer => answer.status === 200 });
}

function tokenFor(email, password) {
    const response = expect(post('/api/v1/tokens', { email, password }), 200, `sign in as ${email}`);

    return JSON.parse(response.body).access_token;
}

function post(path, body, token) {
    return http.post(`${BASE_URL}${path}`, JSON.stringify(body), { headers: headers(token) });
}

function get(path, token) {
    return http.get(`${BASE_URL}${path}`, { headers: headers(token) });
}

function headers(token) {
    const sent = { 'Content-Type': 'application/json' };

    if (token) {
        sent.Authorization = `Bearer ${token}`;
    }

    return sent;
}

function expect(response, status, what) {
    if (response.status !== status) {
        fail(`Could not ${what}: ${response.status} ${response.body}`);
    }

    return response;
}

function uuid() {
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, placeholder => {
        const random = Math.random() * 16 | 0;

        return (placeholder === 'x' ? random : (random & 0x3 | 0x8)).toString(16);
    });
}

function tomorrow() {
    return new Date(Date.now() + 86400000).toISOString().slice(0, 10);
}
