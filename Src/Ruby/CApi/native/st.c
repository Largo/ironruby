/*
 * libironruby-capi: CRuby's st tables (ruby/st.h), the hash table extensions keep their own
 * C data in. IronRuby's Hash is a managed object, so nothing here backs a Ruby Hash: these
 * tables only ever hold what a C extension puts in them.
 *
 * The layout is struct st_table's, which extensions may read (tbl->num_entries): entries in
 * insertion order in an array of 2^entry_power, deleted ones marked with RESERVED_HASH, and an
 * open-addressed index of 2^bin_power bins over them (0 empty, 1 deleted, i + 2 entry i).
 * Iteration is in insertion order, as st.c's.
 */
#include "capi.h"
#include "ruby/st.h"
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* st.c's own: ruby/st.h leaves it incomplete */
typedef st_data_t st_hash_t;
struct st_table_entry {
    st_hash_t hash;
    st_data_t key;
    st_data_t record;
};

#define RESERVED_HASH ((st_hash_t)~(st_hash_t)0)
#define BIN_EMPTY 0
#define BIN_DELETED 1
#define MIN_POWER 2

static void *
st_alloc(size_t size)
{
    void *p = calloc(1, size);
    if (p == NULL && size != 0) rb_memerror();
    return p;
}

static st_index_t entry_capa(const st_table *tab) { return (st_index_t)1 << tab->entry_power; }
static st_index_t bin_count(const st_table *tab) { return (st_index_t)1 << tab->bin_power; }
static int deleted_p(const st_table_entry *e) { return e->hash == RESERVED_HASH; }

static st_hash_t
do_hash(st_data_t key, const st_table *tab)
{
    st_hash_t h = (st_hash_t)tab->type->hash(key);
    return h == RESERVED_HASH ? 0 : h;
}

static int
keys_equal(const st_table *tab, const st_table_entry *e, st_hash_t hash, st_data_t key)
{
    return e->hash == hash && (e->key == key || tab->type->compare(e->key, key) == 0);
}

/* (re)builds the bins over the live entries, after the entries were compacted */
static void
build_bins(st_table *tab)
{
    st_index_t mask = bin_count(tab) - 1;
    memset(tab->bins, 0, bin_count(tab) * sizeof(st_index_t));
    for (st_index_t i = tab->entries_start; i < tab->entries_bound; i++) {
        if (deleted_p(&tab->entries[i])) continue;
        st_index_t b = tab->entries[i].hash & mask;
        while (tab->bins[b] != BIN_EMPTY) b = (b + 1) & mask;
        tab->bins[b] = i + 2;
    }
}

/* room for one more entry: compacts away deleted entries, and doubles when that is not enough */
static void
rebuild(st_table *tab)
{
    unsigned char power = tab->entry_power;
    while (((st_index_t)1 << power) < (tab->num_entries + 1) * 2) power++;
    st_table_entry *entries = st_alloc(((size_t)1 << power) * sizeof(st_table_entry));
    st_index_t n = 0;
    for (st_index_t i = tab->entries_start; i < tab->entries_bound; i++) {
        if (!deleted_p(&tab->entries[i])) entries[n++] = tab->entries[i];
    }
    free(tab->entries);
    tab->entries = entries;
    tab->entries_start = 0;
    tab->entries_bound = n;
    if (power != tab->entry_power) {
        tab->entry_power = power;
        tab->bin_power = power + 1;
        free(tab->bins);
        tab->bins = st_alloc(bin_count(tab) * sizeof(st_index_t));
    }
    tab->rebuilds_num++;
    build_bins(tab);
}

/* the bin of the entry for key, or of the empty bin where it would go (*found 0) */
static st_index_t
find_bin(const st_table *tab, st_hash_t hash, st_data_t key, int *found)
{
    st_index_t mask = bin_count(tab) - 1, b = hash & mask;
    for (;;) {
        st_index_t v = tab->bins[b];
        if (v == BIN_EMPTY) {
            *found = 0;
            return b;
        }
        if (v != BIN_DELETED && keys_equal(tab, &tab->entries[v - 2], hash, key)) {
            *found = 1;
            return b;
        }
        b = (b + 1) & mask;
    }
}

static st_table_entry *
find_entry(st_table *tab, st_data_t key)
{
    int found;
    st_index_t b = find_bin(tab, do_hash(key, tab), key, &found);
    return found ? &tab->entries[tab->bins[b] - 2] : NULL;
}

static void
add_entry(st_table *tab, st_hash_t hash, st_data_t key, st_data_t value)
{
    if (tab->entries_bound == entry_capa(tab)) rebuild(tab);
    int found;
    st_index_t b = find_bin(tab, hash, key, &found);
    st_index_t i = tab->entries_bound++;
    tab->entries[i].hash = hash;
    tab->entries[i].key = key;
    tab->entries[i].record = value;
    tab->bins[b] = i + 2;
    tab->num_entries++;
}

static void
delete_entry(st_table *tab, st_table_entry *e)
{
    st_index_t i = (st_index_t)(e - tab->entries), mask = bin_count(tab) - 1, b = e->hash & mask;
    while (tab->bins[b] != i + 2) b = (b + 1) & mask;
    tab->bins[b] = BIN_DELETED;
    e->hash = RESERVED_HASH;
    e->key = e->record = 0;
    tab->num_entries--;
    while (tab->entries_start < tab->entries_bound && deleted_p(&tab->entries[tab->entries_start])) {
        tab->entries_start++;
    }
}

st_table *
rb_st_init_table_with_size(const struct st_hash_type *type, st_index_t size)
{
    st_table *tab = st_alloc(sizeof(st_table));
    unsigned char power = MIN_POWER;
    while (((st_index_t)1 << power) < size) power++;
    tab->type = type;
    tab->entry_power = power;
    tab->bin_power = power + 1;
    tab->entries = st_alloc(entry_capa(tab) * sizeof(st_table_entry));
    tab->bins = st_alloc(bin_count(tab) * sizeof(st_index_t));
    return tab;
}

st_table *rb_st_init_table(const struct st_hash_type *type) { return rb_st_init_table_with_size(type, 0); }

int rb_st_numcmp(st_data_t x, st_data_t y) { return x != y; }

st_index_t
rb_st_numhash(st_data_t n)
{
    st_index_t h = (st_index_t)n;
    h ^= h >> 33;
    h *= (st_index_t)0xff51afd7ed558ccdULL;
    h ^= h >> 33;
    return h;
}

static int strcmp_func(st_data_t a, st_data_t b) { return strcmp((const char *)a, (const char *)b); }
static int strcasecmp_func(st_data_t a, st_data_t b) { return rb_st_locale_insensitive_strcasecmp((const char *)a, (const char *)b); }
static st_index_t strhash(st_data_t s) { return rb_st_hash((const char *)s, strlen((const char *)s), 0); }

static st_index_t
strcasehash(st_data_t s)
{
    st_index_t h = 0xcbf29ce484222325ULL;
    for (const unsigned char *p = (const unsigned char *)s; *p; p++) {
        h ^= (*p >= 'A' && *p <= 'Z') ? *p + 32 : *p;
        h *= 0x100000001b3ULL;
    }
    return rb_st_hash_end(h);
}

static const struct st_hash_type type_numhash = { rb_st_numcmp, rb_st_numhash };
static const struct st_hash_type type_strhash = { strcmp_func, strhash };
static const struct st_hash_type type_strcasehash = { strcasecmp_func, strcasehash };

st_table *rb_st_init_numtable(void) { return rb_st_init_table(&type_numhash); }
st_table *rb_st_init_numtable_with_size(st_index_t size) { return rb_st_init_table_with_size(&type_numhash, size); }
st_table *rb_st_init_strtable(void) { return rb_st_init_table(&type_strhash); }
st_table *rb_st_init_strtable_with_size(st_index_t size) { return rb_st_init_table_with_size(&type_strhash, size); }
st_table *rb_st_init_strcasetable(void) { return rb_st_init_table(&type_strcasehash); }
st_table *rb_st_init_strcasetable_with_size(st_index_t size) { return rb_st_init_table_with_size(&type_strcasehash, size); }

size_t rb_st_table_size(const struct st_table *tab) { return tab->num_entries; }

size_t
rb_st_memsize(const st_table *tab)
{
    return sizeof(st_table) + entry_capa(tab) * sizeof(st_table_entry) + bin_count(tab) * sizeof(st_index_t);
}

void
rb_st_free_table(st_table *tab)
{
    if (tab == NULL) return;
    free(tab->entries);
    free(tab->bins);
    free(tab);
}

void
rb_st_clear(st_table *tab)
{
    memset(tab->bins, 0, bin_count(tab) * sizeof(st_index_t));
    tab->num_entries = tab->entries_start = tab->entries_bound = 0;
    tab->rebuilds_num++;
}

st_table *
rb_st_copy(st_table *old)
{
    st_table *tab = st_alloc(sizeof(st_table));
    *tab = *old;
    tab->entries = st_alloc(entry_capa(tab) * sizeof(st_table_entry));
    memcpy(tab->entries, old->entries, entry_capa(tab) * sizeof(st_table_entry));
    tab->bins = st_alloc(bin_count(tab) * sizeof(st_index_t));
    memcpy(tab->bins, old->bins, bin_count(tab) * sizeof(st_index_t));
    return tab;
}

int
rb_st_lookup(st_table *tab, st_data_t key, st_data_t *value)
{
    st_table_entry *e = find_entry(tab, key);
    if (e == NULL) return 0;
    if (value) *value = e->record;
    return 1;
}

int
rb_st_get_key(st_table *tab, st_data_t key, st_data_t *result)
{
    st_table_entry *e = find_entry(tab, key);
    if (e == NULL) return 0;
    if (result) *result = e->key;
    return 1;
}

/* 1 when the key was there (its value replaced), 0 when it was added */
int
rb_st_insert(st_table *tab, st_data_t key, st_data_t value)
{
    st_hash_t hash = do_hash(key, tab);
    int found;
    st_index_t b = find_bin(tab, hash, key, &found);
    if (found) {
        tab->entries[tab->bins[b] - 2].record = value;
        return 1;
    }
    add_entry(tab, hash, key, value);
    return 0;
}

/* st_insert, but a new key is stored as func(key) */
int
rb_st_insert2(st_table *tab, st_data_t key, st_data_t value, st_data_t (*func)(st_data_t))
{
    st_hash_t hash = do_hash(key, tab);
    int found;
    st_index_t b = find_bin(tab, hash, key, &found);
    if (found) {
        tab->entries[tab->bins[b] - 2].record = value;
        return 1;
    }
    add_entry(tab, hash, func(key), value);
    return 0;
}

void
rb_st_add_direct(st_table *tab, st_data_t key, st_data_t value)
{
    add_entry(tab, do_hash(key, tab), key, value);
}

int
rb_st_delete(st_table *tab, st_data_t *key, st_data_t *value)
{
    st_table_entry *e = find_entry(tab, *key);
    if (e == NULL) {
        if (value) *value = 0;
        return 0;
    }
    *key = e->key;
    if (value) *value = e->record;
    delete_entry(tab, e);
    return 1;
}

int rb_st_delete_safe(st_table *tab, st_data_t *key, st_data_t *value, st_data_t never) { return rb_st_delete(tab, key, value); }
void rb_st_cleanup_safe(st_table *tab, st_data_t never) { }

int
rb_st_shift(st_table *tab, st_data_t *key, st_data_t *value)
{
    for (st_index_t i = tab->entries_start; i < tab->entries_bound; i++) {
        st_table_entry *e = &tab->entries[i];
        if (deleted_p(e)) continue;
        if (key) *key = e->key;
        if (value) *value = e->record;
        delete_entry(tab, e);
        return 1;
    }
    if (value) *value = 0;
    return 0;
}

/*
 * st.c's st_update: func sees the key and value (*value unset when existing is 0) and answers
 * ST_CONTINUE to store them, ST_DELETE to remove the entry. Returns existing.
 */
int
rb_st_update(st_table *tab, st_data_t key, st_update_callback_func *func, st_data_t arg)
{
    st_table_entry *e = find_entry(tab, key);
    int existing = e != NULL;
    st_data_t k = existing ? e->key : key, v = existing ? e->record : 0;
    unsigned int rebuilds = tab->rebuilds_num;
    int r = func(&k, &v, arg, existing);
    if (existing && tab->rebuilds_num != rebuilds) e = find_entry(tab, key);
    switch (r) {
      case ST_CONTINUE:
        if (!existing) {
            add_entry(tab, do_hash(k, tab), k, v);
        } else if (e) {
            e->key = k;
            e->record = v;
        }
        break;
      case ST_DELETE:
        if (existing && e) delete_entry(tab, e);
        break;
    }
    return existing;
}

/*
 * Walks the entries in insertion order. The callback may change the table: after a rebuild
 * the walk finds its place again by the key it was at, as st.c does.
 */
static int
foreach_impl(st_table *tab, int (*func)(st_data_t, st_data_t, st_data_t, int), st_update_callback_func *replace,
             st_data_t arg, int check)
{
    for (st_index_t i = tab->entries_start; i < tab->entries_bound; i++) {
        st_table_entry *e = &tab->entries[i];
        if (deleted_p(e)) continue;
        st_data_t key = e->key;
        unsigned int rebuilds = tab->rebuilds_num;
        int r = func(key, e->record, arg, 0);
        if (tab->rebuilds_num != rebuilds) {
            e = find_entry(tab, key);
            if (e == NULL) {
                if (check) return func(0, 0, arg, 1) == ST_STOP ? 0 : 1;
                continue;
            }
            i = (st_index_t)(e - tab->entries);
        }
        switch (r) {
          case ST_REPLACE:
            if (replace) {
                st_data_t k = e->key, v = e->record;
                replace(&k, &v, arg, 1);
                e->key = k;
                e->record = v;
            }
            break;
          case ST_CHECK:
            if (!check) break;
            /* fall through: CRuby only checks that the entry is still there */
          case ST_CONTINUE:
            break;
          case ST_STOP:
            return 0;
          case ST_DELETE:
            if (!deleted_p(e)) delete_entry(tab, e);
            break;
        }
    }
    return 0;
}

int
rb_st_foreach(st_table *tab, st_foreach_callback_func *func, st_data_t arg)
{
    return foreach_impl(tab, (int (*)(st_data_t, st_data_t, st_data_t, int))func, NULL, arg, 0);
}

int
rb_st_foreach_check(st_table *tab, st_foreach_check_callback_func *func, st_data_t arg, st_data_t never)
{
    return foreach_impl(tab, func, NULL, arg, 1);
}

int
rb_st_foreach_with_replace(st_table *tab, st_foreach_check_callback_func *func, st_update_callback_func *replace, st_data_t arg)
{
    return foreach_impl(tab, func, replace, arg, 1);
}

static st_index_t
collect(st_table *tab, st_data_t *out, st_index_t size, int values, int skip_never, st_data_t never)
{
    st_index_t n = 0;
    for (st_index_t i = tab->entries_start; i < tab->entries_bound && n < size; i++) {
        st_table_entry *e = &tab->entries[i];
        if (deleted_p(e) || (skip_never && e->key == never)) continue;
        out[n++] = values ? e->record : e->key;
    }
    return n;
}

st_index_t rb_st_keys(st_table *tab, st_data_t *keys, st_index_t size) { return collect(tab, keys, size, 0, 0, 0); }
st_index_t rb_st_keys_check(st_table *tab, st_data_t *keys, st_index_t size, st_data_t never) { return collect(tab, keys, size, 0, 1, never); }
st_index_t rb_st_values(st_table *tab, st_data_t *values, st_index_t size) { return collect(tab, values, size, 1, 0, 0); }
st_index_t rb_st_values_check(st_table *tab, st_data_t *values, st_index_t size, st_data_t never) { return collect(tab, values, size, 1, 1, never); }

int
rb_st_locale_insensitive_strcasecmp(const char *s1, const char *s2)
{
    for (;; s1++, s2++) {
        int c1 = (unsigned char)*s1, c2 = (unsigned char)*s2;
        if (c1 >= 'A' && c1 <= 'Z') c1 += 32;
        if (c2 >= 'A' && c2 <= 'Z') c2 += 32;
        if (c1 != c2) return c1 < c2 ? -1 : 1;
        if (c1 == 0) return 0;
    }
}

int
rb_st_locale_insensitive_strncasecmp(const char *s1, const char *s2, size_t n)
{
    for (size_t i = 0; i < n; i++) {
        int c1 = (unsigned char)s1[i], c2 = (unsigned char)s2[i];
        if (c1 >= 'A' && c1 <= 'Z') c1 += 32;
        if (c2 >= 'A' && c2 <= 'Z') c2 += 32;
        if (c1 != c2) return c1 < c2 ? -1 : 1;
        if (c1 == 0) return 0;
    }
    return 0;
}

/* The hash functions: deterministic within a process, as CRuby's are; not CRuby's numbers. */
st_index_t rb_st_hash_start(st_index_t h) { return h; }

st_index_t
rb_st_hash_uint(st_index_t h, st_index_t i)
{
    h ^= i + 0x9e3779b97f4a7c15ULL + (h << 6) + (h >> 2);
    return h;
}

st_index_t rb_st_hash_uint32(st_index_t h, uint32_t i) { return rb_st_hash_uint(h, i); }

st_index_t
rb_st_hash_end(st_index_t h)
{
    h ^= h >> 33;
    h *= (st_index_t)0xff51afd7ed558ccdULL;
    h ^= h >> 33;
    h *= (st_index_t)0xc4ceb9fe1a85ec53ULL;
    h ^= h >> 33;
    return h;
}

st_index_t
rb_st_hash(const void *ptr, size_t len, st_index_t h)
{
    const unsigned char *p = ptr;
    h ^= 0xcbf29ce484222325ULL;
    for (size_t i = 0; i < len; i++) {
        h ^= p[i];
        h *= 0x100000001b3ULL;
    }
    return rb_st_hash_end(h);
}
