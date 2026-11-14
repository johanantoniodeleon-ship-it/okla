-- okla conectada: tablas en Supabase (SQL Editor > New query > pegar > Run).
-- okla lleva la clave pública del proyecto, así que solo puede AÑADIR filas:
-- sin políticas de lectura, nadie puede ver lo que mandan otros (solo el dueño, desde el panel).

create table if not exists public.informes (
  id      uuid primary key default gen_random_uuid(),
  creado  timestamptz not null default now(),
  equipo  uuid not null,                                        -- número al azar que okla crea al instalarse
  version text not null check (length(version) <= 32),
  datos   jsonb not null check (pg_column_size(datos) < 16000)  -- procesador, gráficas, RAM, pantalla, nivel, errores
);

create table if not exists public.opiniones (
  id      uuid primary key default gen_random_uuid(),
  creado  timestamptz not null default now(),
  equipo  uuid not null,
  version text not null check (length(version) <= 32),
  modo    text check (length(modo) <= 40),
  texto   text not null check (length(texto) between 1 and 2000)
);

alter table public.informes  enable row level security;
alter table public.opiniones enable row level security;

drop policy if exists "okla envia informes" on public.informes;
drop policy if exists "okla envia opiniones" on public.opiniones;
create policy "okla envia informes"  on public.informes  for insert to anon with check (true);
create policy "okla envia opiniones" on public.opiniones for insert to anon with check (true);

revoke all on public.informes, public.opiniones from anon, authenticated;
grant insert on public.informes, public.opiniones to anon;
