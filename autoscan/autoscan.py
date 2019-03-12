# autoscan.py
# used by scanning application to process single nid, or single nid and single location
# exit code is used by scanning application to send signal to scanner.

import argparse
import sys

import sqlite3
import subprocess

from datetime import datetime
import re

#leading characters of a barcode sometimes indicate device type
product_type_prefixes = {
    'T1' : ('TC-1116', '16'),
    'T2' : ('TC-1216', '3'),
    'T3' : ('TC-1120', '39'),
    'T4' : ('TC-1120-RD', '40'),
    'T5' : ('TC-1220', '41'),
    'T6' : ('TC-1220-RD', '42'),
    'TQ' : ('PP-1316', '12'),
    'X1' : ('XR-3100', '14'),
}

#attempts to connect to database
def setup_db():
    try:
        #conn = sqlite3.connect('/var/www/inventory.db')
        conn = sqlite3.connect('/home/jmorrison/inventory-tester-environment/inventory_tester.db')
        print("Connected to database")
    except sqlite3.Error as e:
        print("Error connecting to database: {}".format(e))
        exit(1)

    return conn

def commit(db):
    try:
        db.commit()
        print('commit')
    except sqlite3.Error as e:
        print("Error committing to database: {}".format(e))
        exit(2)

#takes a socket barcode and parses it into a location, socket form and voltage
def parse_socket(barcode):
    location_data = {
        'location'			: '',
        'location_prefix' 	: '',
        'form'				: '',
        'voltage'			: '',
        'read_date'			: datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    }
    
    pillar_format = r'P[NESW][\d]{4}'

    #if on a pillar - do I need others?
    if re.match(pillar_format, barcode):
        location_data['location'] = "{}{}{}".format(barcode[2:4],barcode[1],barcode[4:6])
        location_data['location_prefix'] = "P-"
        location_data['form'] = barcode[6:-3]
        location_data['voltage'] = barcode [-3:]
    else:
        print("Location type not recognized")
        # ***** this should never occur from barcode scanner that passes location ********
        exit(4)

    return location_data

# for now we won't use this method - can have multiple at location
def wipe_socket(db, location_data):
    #when a new location is scanned, set all devices previously at that location to location = None
    db.cursor().execute("UPDATE endpoints SET location='' WHERE location_prefix = ? AND location = ?", (location_data['location_prefix'], location_data['location']))
    commit(db)

def remove_endpoint(db, nid):
    db.cursor().execute("UPDATE endpoints SET location='' WHERE network_id=?", (nid,))
    commit(db)

def check_db_for_endpoint(db, nid):
    n_rows = db.cursor().execute("SELECT COUNT(*) FROM endpoints WHERE network_id=?", (nid,)).fetchone()[0]  # input needs to be tuple 
    return n_rows > 0

#adds endpoint network ID and socket data to the database
def add_endpoint(db, data, nid, nid_prefix, nid_in_db):

    query_variables = [data['location_prefix'], data['location'], data['read_date'], data['form'], data['voltage'], nid]

    #some barcodes have leading characters indicating product type/id, check for those
    if nid_prefix:
        product_name, product_id = product_type_prefixes.get(nid_prefix,('',''))
        query_variables.insert(-1, product_name)
        query_variables.insert(-1, product_id)

    #if the nid exists, update it, if not, insert it
    if nid_in_db:
        if nid_prefix:
            query = "UPDATE endpoints SET location_prefix = ?, location = ?, read_date = ?, socket_form = ?, voltage = ?, product_name = ?, product_id = ? WHERE network_id = ?"
        else:
            query = "UPDATE endpoints SET location_prefix = ?, location = ?, read_date = ?, socket_form = ?, voltage = ? WHERE network_id = ?"

    else:
        if nid_prefix:
            query = "INSERT INTO endpoints (location_prefix, location, read_date, socket_form, voltage, product_name, product_id, network_id) VALUES (?,?,?,?,?,?,?,?)"
        else:
            query = "INSERT INTO endpoints (location_prefix, location, read_date, socket_form, voltage, network_id) VALUES (?,?,?,?,?,?)"

    db.cursor().execute(query, query_variables)
    commit(db)

if __name__=="__main__":
    # establish db connection
    db = setup_db()

    endpoint = sys.argv[1]
    #chop the barcode so it's just the nid
    nid = endpoint[-10:]
    nid_in_db = check_db_for_endpoint(db, nid)

    # Expect 12 digits for most products
    # 10 digits might be WAN NID, could also be ERT type+id
    # 8 digits is ERT
    if len(endpoint) == 12:
        nid_prefix = endpoint[:2] 
    else:
        nid_prefix = None

    # if removing won't have location parameter
    try:
        location = sys.argv[2]
        # # parse location into dictionary
        location_data = parse_socket(location)
        add_endpoint(db, location_data, nid, nid_prefix, nid_in_db)
    except IndexError:
        # if nid not in db, do nothing
        if nid_in_db:
            remove_endpoint(db, nid)