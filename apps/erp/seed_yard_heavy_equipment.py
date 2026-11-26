"""Register demo yard equipment through the ERP write rules."""
import argparse
import os
from contextlib import closing

import app

EQUIPMENT = (
    ('GY-FL-FAB', '강재 가공공장 지게차', '지게차', 8, 'GY-FAB'),
    ('GY-FL-ASSY', '블록 조립공장 지게차', '지게차', 12, 'GY-ASSY1'),
    ('GY-FL-OUT', '선행의장 공장 지게차', '지게차', 5, 'GY-OUTFIT'),
    ('DT-PE-TP', '1도크 PE 블록 트랜스포터', '트랜스포터', 600, 'GY-PE1'),
    ('GY-PE2-TP', '2도크 PE 블록 트랜스포터', '트랜스포터', 600, 'GY-PE2'),
)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    app.DATABASE_URL = os.environ.get('SHIPYARD_DATABASE_URL')
    if not app.DATABASE_URL:
        parser.error('SHIPYARD_DATABASE_URL을 설정하거나 shipyard-postgres로 실행하세요.')
    with closing(app.connect()) as db:
        added = []
        for key, name, kind, capacity, station in EQUIPMENT:
            if not db.execute('SELECT 1 FROM equipment WHERE id=?', (key,)).fetchone():
                app.write(db, 'equipment', {'id': key, 'name': name})
            if not db.execute('SELECT 1 FROM equipment_specs WHERE equipment=?', (key,)).fetchone():
                app.write(db, 'equipment_specs', {
                    'equipment': key, 'equipment_type': kind, 'capacity_t': capacity, 'station': station,
                })
                added.append(key)
        if args.dry_run:
            db.connection.rollback()
        else:
            db.connection.commit()
    if not args.dry_run and added:
        app.bump_external_cache()
    print(('검증: ' if args.dry_run else '반영: ') + (', '.join(added) or '추가 없음'))


if __name__ == '__main__':
    main()
