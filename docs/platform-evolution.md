# Growing the Room Booking MVP: What to Change and When

This document explains possible next steps. These features are not implemented,
and not all of them will be needed. The [MVP design](mvp-design.md) describes the
current system; the [README](../README.md) explains how to run it.

The MVP is a small API with a local SQLite database and three operations. Its
booking transaction prevents overlapping reservations when bookings are written
through the service. Before real production use, it needs access control and a way
to recover from failures, even with few users. Running it across multiple servers
is a separate decision and should follow a demonstrated need.

## 1. Prevent double bookings when running multiple app instances

**When this matters:** the app needs more instances to handle higher traffic,
database writes often wait for locks, or the service must stay available when an
instance fails.

All app instances must use the same database as the source of truth. If each uses
its own local SQLite file, they will have different booking records. SQLite also
allows only one writer at a time, even for different rooms, so adding API instances
alone will not make writes faster.

Moving to PostgreSQL or SQL Server is a natural next step, but changing the EF
database provider alone is not enough. The MVP relies on SQLite's write locking.
The new database needs a tested way to prevent simultaneous requests from booking
the same room at overlapping times.

- PostgreSQL can use an exclusion constraint: a database rule that rejects
  overlapping time ranges for the same room and, if needed, the same tenant
  (organization).
- SQL Server needs suitable locks within the transaction. For example, lock the
  room row so only one transaction can change its bookings at a time. Another
  option is a carefully designed serializable transaction with supporting indexes
  to protect the time range being checked.
- Creating, rescheduling, importing, and administratively changing bookings must
  all prevent overlaps. A lock inside one app process cannot protect other instances.
- Set limits on retries for deadlocks or serialization failures, where the database
  rejects a transaction because of competing work. When a retry is appropriate,
  retry the whole transaction, not just the failed insert.

## 2. Keep queries fast and responses manageable as data grows

**When this matters:** booking history grows, responses become large, or measured
response times become too slow.

The MVP filters and sorts in SQL and has a `(RoomId, StartDateTime)` index. Even so,
some overlap checks still examine many records, and lists without filters have no
size limit.

Add cursor-based pagination with a stable sort order, so clients can fetch results
one page at a time. Define the date range a query may cover and how long to keep
or archive old bookings. Before adding indexes, use realistic data to check the
database's execution plans, which show how it runs each query. Extra indexes also
use storage and slow down writes. If the list response format changes, keep
existing clients working or introduce a new API version.

Set request-size limits, timeouts, and request-rate limits to suit actual usage.
Caching room details may help, but cached availability can be out of date. Always
check availability again in the database transaction that saves the booking.
Read replicas (read-only database copies) can serve booking history when slightly
outdated results are acceptable; they must not decide whether a booking is allowed.

## 3. Control who can access bookings and keep organizations' data separate

**When this matters:** real employees or customers use the app, or multiple
organizations share it.

Use an established login provider. Separately, check who may view each room's
schedule, create or cancel bookings, and manage rooms. Being logged in does not
automatically give a user permission to do all of these things.

If multiple organizations share the app, treat each organization as a tenant.
Determine the tenant from trusted login information, not just a tenant ID sent by
the caller or a filter in the UI. Restrict every query and write to the correct
tenant, and include the tenant in relevant database keys and booking-conflict rules.
Choose shared tables, separate schemas, or separate databases based on how strictly
data must be separated, how it will be managed, and any regulatory requirements.
There is no single layout that fits every case.

Booking titles may contain sensitive information. Decide who can see them, what
may appear in logs, who can access audit records, and how long to keep this data.
Do not log full request bodies by default. Record who changed each booking and
when; created and updated timestamps alone do not provide that history.

## 4. Handle retries, booking changes, and imports safely

**When this matters:** networks are unreliable, clients resend requests, or users
need to reschedule or cancel bookings.

A booking can be saved even if the response never reaches the client. If the
client retries and gets `409 Conflict`, the MVP cannot tell it whether its first
request succeeded or someone else booked the slot.

Add an idempotency key: a client-supplied identifier that lets the server recognize
a repeated request and return its saved result without creating another booking.
Enforce uniqueness for each caller-and-key or tenant-and-key combination in the
database, and define how long to keep the key. Save the key, result, and booking in the same
transaction. Check that repeated requests have the same content; reject a reused
key if the content differs.

When rescheduling, check the new time range and save the change in one transaction.
Exclude the booking being changed from the overlap check. A concurrency token or
ETag can detect whether someone else has edited the booking since it was read,
preventing one edit from overwriting another. It does not replace the overlap check.
Decide whether cancellation deletes the booking or changes its status, and which
statuses still reserve the room.

For a large initial list of rooms, build an explicit import process instead of
adding more entries to `HasData`. Use stable room codes, validate the input, and
define what happens if the same import runs again. State whether imports add rooms,
update them, or both. A room missing from an import must not silently be deleted
if it has bookings.

## 5. Send notifications and sync calendars without blocking bookings

**When this matters:** the app needs email notifications, calendar synchronization,
or events that other systems use.

Do not call external services inside the booking transaction. Instead, save an
outbox entry (a message waiting to be sent) in the same transaction as the booking,
then send it in the background. Delivery may happen more than once, so receiving
systems must handle duplicate messages, retries, and messages arriving out of order.
Monitor failed deliveries and provide a way to recover them.

The booking database remains the source of truth. A failed notification does not
cancel a confirmed booking. For calendar synchronization, decide which system
controls each piece of data and how to resolve differences between the systems.
Do not assume they will always agree.

## 6. Prepare to deploy, monitor, and recover the service

**When this matters:** before any production deployment, even with little traffic.

| Area | What to put in place |
|---|---|
| Deployment | Run database migrations as a coordinated deployment step. When replacing app instances gradually, keep database changes compatible with the old and new app versions |
| Recovery | Decide how quickly service must be restored and how much data loss is acceptable. Automate backups and test restoring them |
| Monitoring | Link logs and traces for the same request. Track response times, booking-conflict rates, database lock waits, failures, and delays in sending outbox messages |
| Reliability | Check whether each instance is healthy and ready to accept traffic. Set limits on database connections, request duration, and retries |
| Configuration | Keep secrets out of source control, validate settings, grant only the database permissions the app needs, and decide where HTTPS/TLS connections terminate and configure that endpoint securely |
| Release checks | Run builds and tests in CI. Test migrations and database-provider behavior in a staging environment similar to production |

When setting up alerts, distinguish normal `409` booking conflicts from unexpected
lock timeouts and server errors. Limit retries so a database outage does not cause
a flood of repeated requests. Running migrations at app startup may be convenient
locally, but multiple instances need a coordinated approach.

## 7. Make changes step by step; split into services only when needed

Keep one application with clearly separated modules until there is a concrete
reason to split it: separate teams need to own or deploy parts independently,
or measurements show a bottleneck that separation would solve. More users alone
do not require microservices, message brokers, or distributed locks.

A practical order, adjusted to actual needs, is:

1. Before production use, add access control, backups and recovery, monitoring,
   and a repeatable deployment process.
2. Before running multiple booking instances, move to a shared server database
   and verify that simultaneous requests cannot cause overlapping bookings.
3. As data grows and clients need safe retries, limit query sizes and add
   idempotency keys.
4. Add new booking operations and background integrations only when needed.
5. Split the application only when the benefits outweigh the added work of
   keeping data consistent across services and operating them.

For each step, identify why it is needed, set a measurable goal, and verify that
overlapping bookings are still prevented. The aim is not to keep every MVP design
choice forever, but to keep bookings correct as the system's needs change.
