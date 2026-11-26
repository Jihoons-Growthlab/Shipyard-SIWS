"""Normalize historic demo hull IDs and add full production scenarios in PostgreSQL."""
import os
import re
from contextlib import closing

import app
import arrange_yard_lines
import seed_fleet
import storage

HULL_IDS = {
    'H101': 'SN0101',
    'DT-DEMO-01': 'SN2050',
    'FLEET-01': 'SN2051',
    'FLEET-02': 'SN2052',
    'FLEET-03': 'SN2053',
}


def hull_references(db):
    return list(db.execute("""
        SELECT source.relname AS table_name, col.attname AS column_name
        FROM pg_constraint fk
        JOIN pg_class source ON source.oid=fk.conrelid
        JOIN pg_attribute col ON col.attrelid=fk.conrelid AND col.attnum=fk.conkey[1]
        WHERE fk.contype='f' AND fk.confrelid='hulls'::regclass
          AND source.relnamespace=current_schema()::regnamespace
          AND cardinality(fk.conkey)=1
        ORDER BY source.relname, col.attname
    """))


def normalize(db):
    references = hull_references(db)
    for old, new in HULL_IDS.items():
        if not db.execute('SELECT 1 FROM hulls WHERE id=?', (old,)).fetchone():
            continue
        app.require(not db.execute('SELECT 1 FROM hulls WHERE id=?', (new,)).fetchone(),
                    f'{new} 호선 ID가 이미 있습니다.')
        db.execute('INSERT INTO hulls(id,name) SELECT ?,name FROM hulls WHERE id=?', (new, old))
        for reference in references:
            table, column = reference['table_name'], reference['column_name']
            if not all(re.fullmatch(r'[a-z][a-z0-9_]*', value) for value in (table, column)):
                raise ValueError(f'예상하지 못한 호선 참조: {table}.{column}')
            db.execute(f'UPDATE "{table}" SET "{column}"=? WHERE "{column}"=?', (new, old))
        db.execute('DELETE FROM hulls WHERE id=?', (old,))


def seed(db):
    normalize(db)
    for ids, prefix, names, offset in (
        (('SN2054', 'SN2055', 'SN2056'), 'DEMO-A', ('해누리', '해담', '해찬'), (0, 560)),
        (('SN2057', 'SN2058', 'SN2059'), 'DEMO-B', ('한결', '한빛', '한울'), (680, 560)),
    ):
        seed_fleet.seed(db, hull_ids=ids, prefix=prefix, names=names, offset=offset)
    arrange_yard_lines.arrange(db)   # lines onto the coastal plain (seed offsets only keep them apart)
    for row in db.execute('SELECT id,name FROM hulls ORDER BY id').fetchall():
        hull, name = row['id'], row['name']
        app.require(re.fullmatch(r'SN[0-9]+', hull), f'SN 번호를 지정해야 하는 호선: {hull}')
        prefix = f'{hull} · '
        if not name.startswith(prefix):
            db.execute('UPDATE hulls SET name=? WHERE id=?', (prefix + name, hull))


if __name__ == '__main__':
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db, db:
        storage.begin_write(db)
        seed(db)
    app.bump_external_cache()
    print('호선 ID·표시 이름 통일 및 가상 생산 데이터 등록 완료')
